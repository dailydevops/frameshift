namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Recognises TUnit test methods, identified by an attribute whose base chain includes the abstract
/// marker base type <c>TUnit.Core.BaseTestAttribute</c>.
/// </summary>
/// <remarks>
/// <para>
/// The marker base type is the right hook because it is the one thing every test marker of the framework
/// has in common, and the only thing that stays true when the framework adds another one.
/// <c>TUnit.Core.TestAttribute</c> is sealed, so it can never be the base of anything, and it is not the
/// only marker: <c>TUnit.Core.DynamicTestBuilderAttribute</c> derives from
/// <c>TUnit.Core.BaseTestAttribute</c> as well and marks a test method just the same. Matching only the
/// sealed <c>TestAttribute</c> would leave those methods unrecognised, their production references would
/// never reach the test-surface manifest, and the production analyzer would then report a test-less
/// production member that is in truth covered.
/// </para>
/// <para>
/// The base type is also what makes a user-defined marker work. A derived marker cannot extend
/// <c>TUnit.Core.BaseTestAttribute</c> itself - its only constructor is internal to the framework - but it
/// can extend <c>TUnit.Core.DynamicTestBuilderAttribute</c>, and walking the base chain to the marker base
/// type recognises it without hard-coding a list of attribute names.
/// </para>
/// <para>
/// Data-source attributes are deliberately not markers. <c>ArgumentsAttribute</c>,
/// <c>MethodDataSourceAttribute</c>, <c>MatrixDataSourceAttribute</c> and <c>ClassDataSourceAttribute</c>
/// derive from <see cref="Attribute" /> rather than from the marker base type, and a marker such as
/// <c>[Test]</c> stays required next to them. The same holds for the attributes that only configure a test,
/// among them <c>RepeatAttribute</c>, <c>CategoryAttribute</c> and <c>SkipAttribute</c>: a method carrying
/// nothing but those is no test.
/// </para>
/// <para>
/// The name-based rule is only a fallback for a compilation in which
/// <c>TUnit.Core.BaseTestAttribute</c> cannot be resolved by its metadata name, which is the state a
/// recogniser created from nothing but the framework's assembly reference is in. It matches the simple name
/// of the marker <em>base</em> type, and it requires the declaring assembly to belong to the framework, so
/// a look-alike <c>BaseTestAttribute</c> of the project itself or of an unrelated package marks nothing. As
/// soon as the base type does resolve, the semantic rule is the whole rule: a same-named type from another
/// namespace never matches it.
/// </para>
/// <para>
/// <em>Counting the test cases</em> of a method is a second, independent question, answered by
/// <see cref="GetTestCaseCount(IMethodSymbol)" />. It is answered off the very same attributes the
/// recognition deliberately ignores, and it is answered without resolving a single well-known type: a data
/// source is identified by the interface <c>TUnit.Core.IDataSourceAttribute</c> the framework puts on every
/// one of them, and a matrix source by the base type <c>TUnit.Core.MatrixAttribute</c> every per-parameter
/// variant derives from. Both checks require the declaring assembly to belong to the framework, so an
/// <c>ArgumentsAttribute</c> of the project itself never contributes a case. An attribute of the framework
/// that is a data source but none of the recognised shapes still contributes its lower bound of one case,
/// which is what keeps a data source added by a future version from being counted as nothing at all.
/// </para>
/// <para>
/// <c>RepeatAttribute</c> deliberately does <em>not</em> multiply the count. The count exists to answer how
/// many distinct input combinations reach a mutation point, and a repeated test runs the same inputs again:
/// <c>[Repeat(5)]</c> next to a single <c>[Arguments]</c> row still exercises one combination, and a mutation
/// that survives that row survives all five executions of it. Counting the repetitions would make exactly
/// those narrow tests look wide and would silence the finding that matters most. The same reasoning applies
/// to the repeat scopes of the class and of the assembly, which are ignored as well.
/// </para>
/// <para>
/// Everything the count cannot read off the source becomes a lower bound rather than a guess, because a
/// lower bound suppresses the reporting downstream while a wrong exact number would not. A data source the
/// framework resolves by executing a member during discovery, a matrix over the values a
/// <see langword="bool" /> or an enum parameter generates automatically, an exclusion taking combinations
/// away again, and a data source on the declaring type multiplying every case of every test method of it are
/// therefore all reported as "at least".
/// </para>
/// </remarks>
internal sealed class TUnitTestMethodRecognizer : ITestMethodRecognizer
{
    /// <summary>
    /// The namespace every attribute the count is read from is declared in.
    /// </summary>
    private const string FrameworkNamespace = "TUnit.Core";

    /// <summary>
    /// The interface the framework implements on every data-source attribute, inline rows included.
    /// </summary>
    private const string DataSourceInterfaceTypeName = "IDataSourceAttribute";

    /// <summary>
    /// The inline data-source attribute; each occurrence of it is exactly one case.
    /// </summary>
    private const string ArgumentsAttributeTypeName = "ArgumentsAttribute";

    /// <summary>
    /// The base type of every per-parameter matrix attribute, the literal-set variants and the ones taking
    /// their values from a range or a method alike.
    /// </summary>
    private const string MatrixAttributeTypeName = "MatrixAttribute";

    /// <summary>
    /// The method-level attribute that enables the matrix of the parameters explicitly.
    /// </summary>
    private const string MatrixDataSourceAttributeTypeName = "MatrixDataSourceAttribute";

    /// <summary>
    /// The method-level attribute taking single combinations out of the matrix again.
    /// </summary>
    private const string MatrixExclusionAttributeTypeName = "MatrixExclusionAttribute";

    /// <summary>
    /// The data-source attribute naming a member of the compilation, whose sequence can be counted when it
    /// is written out literally.
    /// </summary>
    private const string MethodDataSourceAttributeTypeName = "MethodDataSourceAttribute";

    /// <summary>
    /// The property of a matrix attribute taking single values out of its set again.
    /// </summary>
    private const string ExcludingPropertyName = "Excluding";

    /// <summary>
    /// The property naming the type declaring the member of a method data source.
    /// </summary>
    private const string ClassProvidingDataSourcePropertyName = "ClassProvidingDataSource";

    /// <summary>
    /// The property naming the member of a method data source.
    /// </summary>
    private const string MethodNamePropertyName = "MethodNameProvidingDataSource";

    private readonly INamedTypeSymbol? _testAttributeType;
    private readonly INamedTypeSymbol? _baseTestAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="TUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>TUnit.Core.TestAttribute</c> type, or <see langword="null" /> when it could not be
    /// resolved.
    /// </param>
    /// <param name="baseTestAttributeType">
    /// The resolved <c>TUnit.Core.BaseTestAttribute</c> marker base type, or <see langword="null" /> when it
    /// could not be resolved and only the name-based fallback is available.
    /// </param>
    public TUnitTestMethodRecognizer(
        INamedTypeSymbol? testAttributeType,
        INamedTypeSymbol? baseTestAttributeType = null
    )
    {
        _testAttributeType = testAttributeType;
        _baseTestAttributeType = baseTestAttributeType;
    }

    /// <inheritdoc />
    public string FrameworkName => TUnitTestFrameworkProbe.Name;

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public bool IsTestMethod(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        return method.GetAttributes().Any(attribute => IsTestAttribute(attribute.AttributeClass));
    }

    /// <summary>
    /// Counts the test cases <paramref name="method" /> is run as, which is the number of distinct input
    /// combinations it exercises. The result is exact only when every part of it could be read off the
    /// source; see the remarks of <see cref="TUnitTestMethodRecognizer" /> for what turns it into a lower
    /// bound.
    /// </summary>
    /// <param name="method">The test method to count the cases of.</param>
    /// <returns>
    /// The number of cases, exact when every contributing part is exact, otherwise a lower bound. A method
    /// without parameters and without any data source is exactly one case, because its inputs are hardcoded
    /// in its body and are therefore exactly as narrow as a single inline row.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public TestCaseCount GetTestCaseCount(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        var attributes = method.GetAttributes();
        var total = TestCaseCount.Exact(0);
        var hasSource = false;
        var inlineRows = attributes.Count(attribute => IsInlineData(attribute.AttributeClass));

        if (inlineRows > 0)
        {
            total = total.Add(TestCaseCount.Exact(inlineRows));
            hasSource = true;
        }

        var hasMatrix = TryGetMatrixCount(method, attributes, out var matrix);

        if (hasMatrix)
        {
            total = total.Add(matrix);
            hasSource = true;
        }

        var sources = attributes.Where(attribute => IsCountedDataSource(attribute.AttributeClass, hasMatrix));

        foreach (var attribute in sources)
        {
            total = total.Add(GetDataSourceCount(method, attribute));
            hasSource = true;
        }

        return Complete(method, total, hasSource);
    }

    /// <summary>
    /// Turns the accumulated contributions into the answer: a method without any data source at all is one
    /// case, and a data source on the declaring type multiplies every case of it by an unknown factor.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <param name="total">The sum of the contributions of the method itself.</param>
    /// <param name="hasSource">Whether the method carries any data source at all.</param>
    /// <returns>The number of cases.</returns>
    private static TestCaseCount Complete(IMethodSymbol method, TestCaseCount total, bool hasSource)
    {
        var count = hasSource ? total : GetSourcelessCount(method);

        return HasDataSourceOnDeclaringType(method) ? TestCaseCount.AtLeast(count.Value) : count;
    }

    /// <summary>
    /// Counts a method the framework runs without handing it anything: without parameters that is exactly
    /// one case, whose inputs are hardcoded in the body. A method that does have parameters but no data
    /// source is nothing the framework can run at all, so no exact statement is made about it.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <returns>The number of cases.</returns>
    private static TestCaseCount GetSourcelessCount(IMethodSymbol method) =>
        method.Parameters.Length == 0 ? TestCaseCount.Exact(1) : TestCaseCount.AtLeast(1);

    /// <summary>
    /// Counts the cross product of the matrix values of the parameters of <paramref name="method" />, which
    /// exists as soon as one parameter carries a matrix attribute - the method-level
    /// <c>MatrixDataSourceAttribute</c> only states the same thing explicitly and is then absorbed by this
    /// count. A parameter without a matrix attribute takes its values from its type, which is not written
    /// out anywhere, and an exclusion makes the cross product an upper rather than a lower bound.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <param name="attributes">The attributes of the method.</param>
    /// <param name="count">The cross product, valid only when the method has a matrix at all.</param>
    /// <returns><see langword="true" /> when the method has a matrix over its parameters.</returns>
    private static bool TryGetMatrixCount(
        IMethodSymbol method,
        ImmutableArray<AttributeData> attributes,
        out TestCaseCount count
    )
    {
        count = TestCaseCount.Exact(0);

        var sources = GetMatrixSources(method);

        if (sources.All(source => source is null))
        {
            return false;
        }

        if (HasExclusion(attributes, sources))
        {
            count = TestCaseCount.AtLeast(1);

            return true;
        }

        var product = 1L;
        var isExact = true;

        foreach (var (size, isSetExact) in sources.Select(GetMatrixSet))
        {
            product *= size;
            isExact = isExact && isSetExact;
        }

        count = isExact ? TestCaseCount.Exact(ToCount(product)) : TestCaseCount.AtLeast(ToCount(product));

        return true;
    }

    /// <summary>
    /// Collects the matrix attribute of every parameter of <paramref name="method" />, in parameter order and
    /// holding <see langword="null" /> for a parameter without one, because a parameter contributing no
    /// value set of its own is exactly what makes a cross product inexact.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <returns>One entry per parameter.</returns>
    private static ImmutableArray<AttributeData?> GetMatrixSources(IMethodSymbol method) =>
        [
            .. method.Parameters.Select(parameter =>
                parameter.GetAttributes().FirstOrDefault(attribute => IsMatrixSource(attribute.AttributeClass))
            ),
        ];

    /// <summary>
    /// Reads the value set of a single matrix attribute. The literal variants pass their values as one array
    /// argument, which is the only shape whose size is written out: the variants taking a range or the name
    /// of a method contribute an unknown factor, as does a parameter without a matrix attribute.
    /// </summary>
    /// <param name="attribute">The matrix attribute of the parameter, or <see langword="null" />.</param>
    /// <returns>The size of the value set and whether that size is exact.</returns>
    private static (int Size, bool IsExact) GetMatrixSet(AttributeData? attribute)
    {
        if (attribute is null || attribute.ConstructorArguments.Length != 1)
        {
            return (1, false);
        }

        var values = attribute.ConstructorArguments[0];

        if (values.Kind != TypedConstantKind.Array || values.IsNull || values.Values.Length == 0)
        {
            return (1, false);
        }

        return (values.Values.Length, values.Values.All(IsLiteral));
    }

    /// <summary>
    /// Determines whether the matrix described by <paramref name="attributes" /> and <paramref name="sources" />
    /// has combinations or values taken out of it, which no longer makes the cross product a lower bound of
    /// anything.
    /// </summary>
    /// <param name="attributes">The attributes of the counted method.</param>
    /// <param name="sources">
    /// The matrix attribute of every parameter, holding <see langword="null" /> for a parameter without one.
    /// </param>
    /// <returns><see langword="true" /> when combinations or values are excluded.</returns>
    private static bool HasExclusion(ImmutableArray<AttributeData> attributes, ImmutableArray<AttributeData?> sources)
    {
        var excluded = attributes.Any(attribute =>
            IsFrameworkAttribute(attribute.AttributeClass, MatrixExclusionAttributeTypeName)
        );

        return excluded || sources.Any(source => source is not null && HasExcludedValues(source));
    }

    /// <summary>
    /// Determines whether a matrix attribute takes single values out of its own set again.
    /// </summary>
    /// <param name="attribute">The matrix attribute of a parameter.</param>
    /// <returns><see langword="true" /> when it excludes values.</returns>
    private static bool HasExcludedValues(AttributeData attribute)
    {
        var excluding = GetNamedArgument(attribute, ExcludingPropertyName);

        return excluding.Kind == TypedConstantKind.Array && !excluding.IsNull && excluding.Values.Length > 0;
    }

    /// <summary>
    /// Counts the cases of a single data source that is neither an inline row nor the matrix: the sequence of
    /// the named member when it is written out literally, and a lower bound of one case otherwise, because
    /// the framework resolves the concrete number by executing the member during discovery.
    /// </summary>
    /// <param name="method">The counted method, whose declaring type is where a member name is looked up.</param>
    /// <param name="attribute">The data-source attribute.</param>
    /// <returns>The number of cases the data source contributes.</returns>
    private static TestCaseCount GetDataSourceCount(IMethodSymbol method, AttributeData attribute)
    {
        if (
            IsFrameworkAttribute(attribute.AttributeClass, MethodDataSourceAttributeTypeName)
            && TryGetLiteralSequenceLength(method, attribute, out var length)
        )
        {
            return TestCaseCount.Exact(length);
        }

        return TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Resolves the member a method data source names and counts its sequence, which succeeds only when the
    /// member is declared in this compilation, is unambiguous, and returns or holds a sequence of literals.
    /// </summary>
    /// <param name="method">The counted method, whose declaring type is the default container.</param>
    /// <param name="attribute">The data-source attribute naming the member.</param>
    /// <param name="length">The length of the sequence, valid only when the member could be counted.</param>
    /// <returns><see langword="true" /> when the sequence is statically enumerable.</returns>
    private static bool TryGetLiteralSequenceLength(IMethodSymbol method, AttributeData attribute, out int length)
    {
        length = 0;

        var memberName = GetMemberName(attribute);

        if (memberName is null)
        {
            return false;
        }

        var members = FindMembers(GetContainer(method, attribute), memberName);

        return members.Length == 1 && TryGetSequenceLength(members[0], out length);
    }

    /// <summary>
    /// Collects the members of <paramref name="container" /> and of its base types carrying
    /// <paramref name="memberName" />, so that an ambiguous name is recognised as such instead of being
    /// counted off the wrong member.
    /// </summary>
    /// <param name="container">The type declaring the member.</param>
    /// <param name="memberName">The name of the member.</param>
    /// <returns>Every matching member, possibly none.</returns>
    private static ImmutableArray<ISymbol> FindMembers(INamedTypeSymbol container, string memberName)
    {
        var builder = ImmutableArray.CreateBuilder<ISymbol>();

        for (var current = container; current is not null; current = current.BaseType)
        {
            builder.AddRange(current.GetMembers(memberName));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Counts the sequence a member is declared as: the expression of an expression-bodied method or
    /// property, the single returned expression of a block-bodied one, or the initialiser of a property or a
    /// field. Anything else - a loop, a condition, a call, or a member that only exists in metadata - is not
    /// statically enumerable.
    /// </summary>
    /// <param name="member">The member the data source names.</param>
    /// <param name="length">The length of the sequence, valid only on success.</param>
    /// <returns><see langword="true" /> when the sequence could be counted.</returns>
    private static bool TryGetSequenceLength(ISymbol member, out int length)
    {
        length = 0;

        var references = member.DeclaringSyntaxReferences;

        if (references.Length != 1)
        {
            return false;
        }

        var expression = GetDeclaredExpression(references[0].GetSyntax());

        return expression is not null && TryCountLiteralElements(expression, out length);
    }

    /// <summary>
    /// Picks the one expression a member declaration hands its sequence over as.
    /// </summary>
    /// <param name="declaration">The declaration of the member.</param>
    /// <returns>The expression, or <see langword="null" /> when the declaration has none.</returns>
    private static ExpressionSyntax? GetDeclaredExpression(SyntaxNode declaration) =>
        declaration switch
        {
            MethodDeclarationSyntax method => method.ExpressionBody?.Expression ?? GetReturnedExpression(method.Body),
            PropertyDeclarationSyntax property => property.ExpressionBody?.Expression ?? property.Initializer?.Value,
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            _ => null,
        };

    /// <summary>
    /// Reads the expression of a block body that does nothing but return one, which is the same shape as an
    /// expression body. A body doing anything else is not statically enumerable.
    /// </summary>
    /// <param name="body">The block body of the member, which may be <see langword="null" />.</param>
    /// <returns>The returned expression, or <see langword="null" />.</returns>
    private static ExpressionSyntax? GetReturnedExpression(BlockSyntax? body)
    {
        if (body is null || body.Statements.Count != 1)
        {
            return null;
        }

        return body.Statements[0] is ReturnStatementSyntax statement ? statement.Expression : null;
    }

    /// <summary>
    /// Counts the elements of a sequence written out in source: a collection expression, an array creation
    /// with an initialiser, or a collection initialiser. Every element has to be a literal, because only
    /// then is the sequence known to be exactly this long without evaluating anything.
    /// </summary>
    /// <param name="expression">The expression the member hands its sequence over as.</param>
    /// <param name="length">The number of elements, valid only on success.</param>
    /// <returns><see langword="true" /> when the elements could be counted.</returns>
    private static bool TryCountLiteralElements(ExpressionSyntax expression, out int length)
    {
        length = 0;

        if (expression is CollectionExpressionSyntax collection)
        {
            length = collection.Elements.Count;

            return collection.Elements.All(IsLiteralElement);
        }

        var initializer = GetInitializer(expression);

        if (initializer is null)
        {
            return false;
        }

        length = initializer.Expressions.Count;

        return initializer.Expressions.All(IsLiteralExpression);
    }

    /// <summary>
    /// Picks the initialiser of the shapes that carry one.
    /// </summary>
    /// <param name="expression">The expression the member hands its sequence over as.</param>
    /// <returns>The initialiser, or <see langword="null" /> when the expression has none.</returns>
    private static InitializerExpressionSyntax? GetInitializer(ExpressionSyntax expression) =>
        expression switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer,
            ImplicitArrayCreationExpressionSyntax array => array.Initializer,
            InitializerExpressionSyntax initializer => initializer,
            BaseObjectCreationExpressionSyntax creation => creation.Initializer,
            _ => null,
        };

    /// <summary>
    /// Determines whether an element of a collection expression is a literal, which a spread element never
    /// is.
    /// </summary>
    /// <param name="element">The element to inspect.</param>
    /// <returns><see langword="true" /> when the element is a literal.</returns>
    private static bool IsLiteralElement(CollectionElementSyntax element) =>
        element is ExpressionElementSyntax expression && IsLiteralExpression(expression.Expression);

    /// <summary>
    /// Determines whether an expression is a literal, counting a signed number as one. No further constant
    /// folding is attempted: a named constant, a call or an operation is not a literal here.
    /// </summary>
    /// <param name="expression">The expression to inspect.</param>
    /// <returns><see langword="true" /> when the expression is a literal.</returns>
    private static bool IsLiteralExpression(ExpressionSyntax expression) =>
        expression switch
        {
            LiteralExpressionSyntax => true,
            PrefixUnaryExpressionSyntax unary => IsLiteralExpression(unary.Operand),
            _ => false,
        };

    /// <summary>
    /// Determines whether a value of an attribute argument is a literal one. A type or a nested array is
    /// none, and a value the compiler could not bind is none either.
    /// </summary>
    /// <param name="value">The attribute argument value to inspect.</param>
    /// <returns><see langword="true" /> when the value is a literal.</returns>
    private static bool IsLiteral(TypedConstant value) =>
        value.Kind == TypedConstantKind.Primitive || value.Kind == TypedConstantKind.Enum;

    /// <summary>
    /// Reads the name of the member a method data source names, from its constructor argument or from the
    /// property carrying the same information.
    /// </summary>
    /// <param name="attribute">The data-source attribute.</param>
    /// <returns>The member name, or <see langword="null" /> when the attribute names none.</returns>
    private static string? GetMemberName(AttributeData attribute)
    {
        var argument = attribute
            .ConstructorArguments.Where(value => value.Kind == TypedConstantKind.Primitive)
            .Select(value => value.Value as string)
            .FirstOrDefault(value => value is { Length: > 0 });

        return argument ?? GetNamedArgument(attribute, MethodNamePropertyName).Value as string;
    }

    /// <summary>
    /// Reads the type declaring the member a method data source names, which is the declaring type of the
    /// counted method unless the attribute names another one.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <param name="attribute">The data-source attribute.</param>
    /// <returns>The type the member is looked up in.</returns>
    private static INamedTypeSymbol GetContainer(IMethodSymbol method, AttributeData attribute)
    {
        var argument = attribute
            .ConstructorArguments.Where(value => value.Kind == TypedConstantKind.Type)
            .Select(value => value.Value as INamedTypeSymbol)
            .FirstOrDefault(value => value is not null);

        return argument
            ?? GetNamedArgument(attribute, ClassProvidingDataSourcePropertyName).Value as INamedTypeSymbol
            ?? method.ContainingType;
    }

    /// <summary>
    /// Reads a named argument of an attribute, which is the default value when the attribute does not set it.
    /// </summary>
    /// <param name="attribute">The attribute to read from.</param>
    /// <param name="name">The name of the argument.</param>
    /// <returns>The value of the argument.</returns>
    private static TypedConstant GetNamedArgument(AttributeData attribute, string name) =>
        attribute
            .NamedArguments.FirstOrDefault(argument => string.Equals(argument.Key, name, StringComparison.Ordinal))
            .Value;

    /// <summary>
    /// Determines whether the declaring type of <paramref name="method" />, or one of its base types, carries
    /// a data source. Such a source multiplies every case of every test method of the type by the number of
    /// instances it produces, which is not written out in the source of the method itself.
    /// </summary>
    /// <param name="method">The counted method.</param>
    /// <returns><see langword="true" /> when the declaring type carries a data source.</returns>
    private static bool HasDataSourceOnDeclaringType(IMethodSymbol method)
    {
        for (var current = method.ContainingType; current is not null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(attribute => IsDataSource(attribute.AttributeClass)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a data source is counted by the loop over the attributes of the method, which the
    /// inline rows and an absorbed matrix are not.
    /// </summary>
    /// <param name="attributeClass">The attribute to classify.</param>
    /// <param name="hasMatrix">Whether the cross product of the parameters was already counted.</param>
    /// <returns><see langword="true" /> when the attribute contributes a case of its own.</returns>
    private static bool IsCountedDataSource(INamedTypeSymbol? attributeClass, bool hasMatrix)
    {
        if (!IsDataSource(attributeClass) || IsInlineData(attributeClass))
        {
            return false;
        }

        return !hasMatrix || !IsFrameworkAttribute(attributeClass, MatrixDataSourceAttributeTypeName);
    }

    /// <summary>
    /// Determines whether an attribute is a data source of the framework, judged by the interface the
    /// framework implements on every one of them.
    /// </summary>
    /// <param name="attributeClass">The attribute to classify.</param>
    /// <returns><see langword="true" /> when the attribute is a data source.</returns>
    private static bool IsDataSource(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null
        && attributeClass.AllInterfaces.Any(implemented =>
            IsFrameworkType(implemented.OriginalDefinition, DataSourceInterfaceTypeName)
        );

    /// <summary>
    /// Determines whether an attribute is one inline row of data.
    /// </summary>
    /// <param name="attributeClass">The attribute to classify.</param>
    /// <returns><see langword="true" /> when the attribute is an inline row.</returns>
    private static bool IsInlineData(INamedTypeSymbol? attributeClass) =>
        IsFrameworkAttribute(attributeClass, ArgumentsAttributeTypeName);

    /// <summary>
    /// Determines whether an attribute of a parameter contributes a set of matrix values, which every
    /// variant of the framework's matrix attribute does.
    /// </summary>
    /// <param name="attributeClass">The attribute to classify.</param>
    /// <returns><see langword="true" /> when the attribute is a matrix source.</returns>
    private static bool IsMatrixSource(INamedTypeSymbol? attributeClass) =>
        IsFrameworkAttribute(attributeClass, MatrixAttributeTypeName);

    /// <summary>
    /// Determines whether an attribute is, or derives from, the named attribute of the framework.
    /// </summary>
    /// <param name="attributeClass">The attribute to classify.</param>
    /// <param name="name">The simple name of the framework attribute.</param>
    /// <returns><see langword="true" /> when the base chain reaches that attribute.</returns>
    private static bool IsFrameworkAttribute(INamedTypeSymbol? attributeClass, string name)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (IsFrameworkType(current.OriginalDefinition, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether a type is the named type of the framework, which requires the namespace and the
    /// declaring assembly to match as well: a same-named attribute of the project itself contributes nothing
    /// to any count.
    /// </summary>
    /// <param name="type">The type to classify.</param>
    /// <param name="name">The simple name of the framework type.</param>
    /// <returns><see langword="true" /> when the type is that framework type.</returns>
    private static bool IsFrameworkType(INamedTypeSymbol type, string name) =>
        string.Equals(type.Name, name, StringComparison.Ordinal)
        && string.Equals(type.ContainingNamespace?.ToDisplayString(), FrameworkNamespace, StringComparison.Ordinal)
        && TUnitTestFrameworkProbe.IsFrameworkAssembly(type.ContainingAssembly);

    /// <summary>
    /// Narrows a cross product to the range a count is expressed in, so that an absurdly large matrix
    /// reports the largest number it can rather than overflowing into a negative one.
    /// </summary>
    /// <param name="product">The computed cross product.</param>
    /// <returns>The number of cases.</returns>
    private static int ToCount(long product) => product > int.MaxValue ? int.MaxValue : (int)product;

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (IsResolvedMarkerType(definition) || IsFrameworkMarkerName(definition))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Compares <paramref name="definition" /> against the types that were resolved for the compilation.
    /// The marker base type carries the recognition; the sealed <c>TestAttribute</c> is compared as well so
    /// that a caller holding only that type still recognises a plain <c>[Test]</c>.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is one of the resolved types; otherwise
    /// <see langword="false" />.
    /// </returns>
    private bool IsResolvedMarkerType(INamedTypeSymbol definition) =>
        Matches(definition, _baseTestAttributeType) || Matches(definition, _testAttributeType);

    /// <summary>
    /// Compares <paramref name="definition" /> against <paramref name="resolved" /> semantically, so that a
    /// same-named type from another namespace or another assembly never matches.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <param name="resolved">The resolved type to compare against, which may be <see langword="null" />.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is that very type; otherwise <see langword="false" />.
    /// </returns>
    private static bool Matches(INamedTypeSymbol definition, INamedTypeSymbol? resolved) =>
        resolved is not null && SymbolEqualityComparer.Default.Equals(definition, resolved);

    /// <summary>
    /// The fallback for the state in which the marker base type could not be resolved: the simple name of
    /// that base type, declared by an assembly of the framework.
    /// </summary>
    /// <param name="definition">The attribute definition of the current step of the base chain.</param>
    /// <returns>
    /// <see langword="true" /> if the definition is a framework marker base type by name; otherwise
    /// <see langword="false" />.
    /// </returns>
    private bool IsFrameworkMarkerName(INamedTypeSymbol definition) =>
        _baseTestAttributeType is null
        && string.Equals(definition.Name, TUnitTestFrameworkProbe.BaseTestAttributeTypeName, StringComparison.Ordinal)
        && TUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
