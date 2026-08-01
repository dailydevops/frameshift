namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises MSTest test methods, identified by an attribute that either is
/// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c>, derives from it, or is named
/// <c>TestMethodAttribute</c> and declared in an MSTest assembly.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the well-known test attributes of other frameworks, <c>TestMethodAttribute</c> is not sealed:
/// MSTest itself derives <c>DataTestMethodAttribute</c> and <c>STATestMethodAttribute</c> from it, and
/// extending it is the documented way of writing a custom test attribute. Walking the base chain
/// therefore covers both the framework's own specialisations and user-defined ones, without hard-coding a
/// list of attribute names.
/// </para>
/// <para>
/// The base type genuinely is the marker here, so no second one has to be looked for. Every attribute of
/// the framework that makes a method a test derives from <c>TestMethodAttribute</c>, up to and including
/// MSTest 4, which added no marker outside that chain. <c>DataRowAttribute</c> and
/// <c>DynamicDataAttribute</c> in particular are not markers: they derive from <see cref="Attribute" />
/// and implement <c>ITestDataSource</c>, they only feed arguments to a method that is already a test, and
/// a method carrying nothing but one of them is not discovered by MSTest at all.
/// </para>
/// <para>
/// The name-based rule is the fallback for a compilation in which
/// <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> cannot be resolved by its
/// metadata name — because it is declared more than once and therefore ambiguous, or because the
/// compilation only carries the MSTest assembly reference. Requiring the declaring assembly to belong to
/// the framework keeps a look-alike <c>TestMethodAttribute</c> of the project itself from being mistaken
/// for a test attribute, so a recogniser left with nothing but this rule fails closed: it finds no tests
/// rather than the wrong ones.
/// </para>
/// <para>
/// <c>TestClassAttribute</c> marks the declaring type, not the method, and does not derive from
/// <c>TestMethodAttribute</c>. A method is consequently never recognised because of it — only an
/// attribute on the method itself makes the method a test. The same holds for the fixture attributes
/// <c>TestInitializeAttribute</c> and <c>TestCleanupAttribute</c>, which sit on a method but derive
/// straight from <see cref="Attribute" />.
/// </para>
/// <para>
/// <em>Counting test cases</em> is a second, independent judgement, and MSTest keeps it simple: the
/// framework feeds arguments to a test method through the attributes implementing <c>ITestDataSource</c>,
/// and up to and including MSTest 4 it ships exactly two of them. <c>DataRowAttribute</c> is applicable
/// multiple times and contributes exactly one case per application, whatever its arguments are.
/// <c>DynamicDataAttribute</c> is applicable multiple times too and contributes the length of the member
/// it names, exactly when that length is written down in the declaration of the member and as a lower
/// bound of one otherwise — resolving it further would mean evaluating the member, which is what MSTest
/// does at discovery time and what an analyzer must not do. Carrying both is the sum, and a
/// <c>[TestMethod]</c> without any of them is exactly one case, its inputs being hardcoded in the body.
/// </para>
/// <para>
/// Both are matched along their base chain, because neither is sealed and deriving from
/// <c>DataRowAttribute</c> to describe a row differently is a common thing to do. Any other attribute
/// implementing the framework's <c>ITestDataSource</c> — a user-written source, or one a later version of
/// MSTest adds — is a lower bound of one: it supplies rows this count cannot see, and a lower bound is the
/// answer that suppresses a finding rather than inventing one. <c>DataSourceAttribute</c> is counted the
/// same way although it implements no such interface: it names an external table, so its rows are neither
/// visible here nor ever guaranteed to be a single one.
/// </para>
/// <para>
/// Two MSTest features deliberately change nothing. <c>RetryAttribute</c>, added in MSTest 4, re-runs a
/// failed case with the very same arguments, so it adds no input combination — and an input combination is
/// the only thing this count is about. The <c>UnfoldingStrategy</c> of <c>TestMethodAttribute</c> only
/// decides whether the rows of a data source are reported as one test or as many; they all execute either
/// way, so the number of input combinations is the same.
/// </para>
/// </remarks>
internal sealed class MSTestTestMethodRecognizer : ITestMethodRecognizer
{
    /// <summary>
    /// The simple type name of the attribute contributing one inline case per application.
    /// </summary>
    private const string DataRowAttributeTypeName = "DataRowAttribute";

    /// <summary>
    /// The simple type name of the attribute contributing the cases of a referenced member.
    /// </summary>
    private const string DynamicDataAttributeTypeName = "DynamicDataAttribute";

    /// <summary>
    /// The simple type name of the attribute naming an external table as the source of the cases.
    /// </summary>
    private const string DataSourceAttributeTypeName = "DataSourceAttribute";

    /// <summary>
    /// The simple type name of the interface an attribute implements to feed arguments to a test method.
    /// </summary>
    private const string DataSourceInterfaceTypeName = "ITestDataSource";

    private readonly INamedTypeSymbol? _testAttributeType;

    /// <summary>
    /// Initializes a new instance of the <see cref="MSTestTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testAttributeType">
    /// The resolved <c>Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute</c> type, or
    /// <see langword="null" /> when only the name-based rule is available.
    /// </param>
    public MSTestTestMethodRecognizer(INamedTypeSymbol? testAttributeType) => _testAttributeType = testAttributeType;

    /// <inheritdoc />
    public string FrameworkName => MSTestTestFrameworkProbe.Name;

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

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="method" /> is <see langword="null" />.</exception>
    public TestCaseCount GetTestCaseCount(IMethodSymbol method)
    {
        if (method is null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        var counts = GetDataSourceCaseCounts(method);

        return counts.IsEmpty ? TestCaseCount.Exact(1) : counts.Aggregate((left, right) => left.Add(right));
    }

    /// <summary>
    /// Counts the cases every data source attribute of <paramref name="method" /> contributes.
    /// </summary>
    /// <param name="method">The test method to inspect.</param>
    /// <returns>
    /// One count per data source attribute, which is empty when the method carries none and its single
    /// hardcoded case is therefore the answer.
    /// </returns>
    private static ImmutableArray<TestCaseCount> GetDataSourceCaseCounts(IMethodSymbol method)
    {
        var attributes = method.GetAttributes();
        var builder = ImmutableArray.CreateBuilder<TestCaseCount>(attributes.Length);

        foreach (var attribute in attributes)
        {
            if (IsFrameworkAttribute(attribute.AttributeClass, DataRowAttributeTypeName))
            {
                builder.Add(TestCaseCount.Exact(1));
            }
            else if (IsFrameworkAttribute(attribute.AttributeClass, DynamicDataAttributeTypeName))
            {
                builder.Add(GetDynamicDataCaseCount(attribute, method.ContainingType));
            }
            else if (IsUnreadableDataSource(attribute.AttributeClass))
            {
                builder.Add(TestCaseCount.AtLeast(1));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the case count of a <c>DynamicDataAttribute</c>, which names its source by a member name and
    /// an optional declaring type.
    /// </summary>
    /// <param name="attribute">The data source attribute to read.</param>
    /// <param name="containingType">The type the referenced member is looked up in by default.</param>
    /// <returns>
    /// The exact length of the referenced sequence, or a lower bound of one when no member is named or its
    /// length cannot be read off the syntax.
    /// </returns>
    private static TestCaseCount GetDynamicDataCaseCount(AttributeData attribute, INamedTypeSymbol? containingType)
    {
        var arguments = attribute.ConstructorArguments;
        var sourceType = arguments.FirstOrDefault(argument => argument.Kind == TypedConstantKind.Type);
        var sourceName = arguments.FirstOrDefault(argument => argument.Value is string).Value as string;

        if (string.IsNullOrEmpty(sourceName))
        {
            return TestCaseCount.AtLeast(1);
        }

        var length = SequenceLengthReader.TryGetSequenceLength(
            sourceType.Value as INamedTypeSymbol ?? containingType,
            sourceName!
        );

        return length.HasValue ? TestCaseCount.Exact(length.Value) : TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> supplies cases whose number this count cannot
    /// read: any other implementation of the framework's <c>ITestDataSource</c>, and the external table of
    /// <c>DataSourceAttribute</c>.
    /// </summary>
    /// <param name="attributeClass">The attribute type to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the attribute contributes an unknown number of cases; otherwise
    /// <see langword="false" />.
    /// </returns>
    private static bool IsUnreadableDataSource(INamedTypeSymbol? attributeClass) =>
        IsFrameworkAttribute(attributeClass, DataSourceAttributeTypeName)
        || (
            attributeClass is not null
            && attributeClass.AllInterfaces.Any(interfaceType =>
                string.Equals(
                    interfaceType.OriginalDefinition.Name,
                    DataSourceInterfaceTypeName,
                    StringComparison.Ordinal
                ) && MSTestTestFrameworkProbe.IsFrameworkAssembly(interfaceType.OriginalDefinition.ContainingAssembly)
            )
        );

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> is, or derives from, a framework attribute of
    /// the given simple name.
    /// </summary>
    /// <param name="attributeClass">The attribute type to classify.</param>
    /// <param name="typeName">The simple type name to match.</param>
    /// <returns>
    /// <see langword="true" /> if the attribute matches; otherwise <see langword="false" />.
    /// </returns>
    private static bool IsFrameworkAttribute(INamedTypeSymbol? attributeClass, string typeName)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (
                string.Equals(definition.Name, typeName, StringComparison.Ordinal)
                && MSTestTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly)
            )
            {
                return true;
            }
        }

        return false;
    }

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;

            if (_testAttributeType is not null && SymbolEqualityComparer.Default.Equals(definition, _testAttributeType))
            {
                return true;
            }

            if (IsFrameworkTestAttributeName(definition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFrameworkTestAttributeName(INamedTypeSymbol definition) =>
        string.Equals(definition.Name, MSTestTestFrameworkProbe.TestAttributeTypeName, StringComparison.Ordinal)
        && MSTestTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
