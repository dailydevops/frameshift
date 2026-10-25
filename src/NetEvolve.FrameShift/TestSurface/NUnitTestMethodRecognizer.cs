namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises NUnit test methods, identified by an attribute implementing
/// <c>NUnit.Framework.Interfaces.ISimpleTestBuilder</c> or <c>NUnit.Framework.Interfaces.ITestBuilder</c>.
/// </summary>
/// <remarks>
/// <para>
/// Those two interfaces are what NUnit itself keys on: its method-level discovery accepts a method
/// exactly when one of the attributes on it implements either interface. Matching them therefore matches
/// the framework's own rule, instead of approximating it with a list of attribute names.
/// </para>
/// <para>
/// The distinction matters. NUnit's test attributes are siblings under
/// <c>NUnit.Framework.NUnitAttribute</c>, which is no test marker at all — <c>SetUpAttribute</c> and
/// <c>TestFixtureAttribute</c> derive from it as well — so there is no base attribute type to walk
/// towards, and a list of the obvious names is always short of the truth. Besides <c>TestAttribute</c>,
/// <c>TestCaseAttribute</c> and <c>TestCaseSourceAttribute</c>, NUnit ships <c>TheoryAttribute</c> and the
/// combining strategies <c>CombinatorialAttribute</c>, <c>PairwiseAttribute</c> and
/// <c>SequentialAttribute</c>, each of which builds tests from a method on its own and none of which a
/// three-name list catches.
/// </para>
/// <para>
/// Matching the interfaces also covers derivation for free: a user-written or framework-written
/// specialisation inherits the interface from the attribute it derives from, and the interfaces of base
/// types are part of <see cref="ITypeSymbol.AllInterfaces" />.
/// </para>
/// <para>
/// Attributes that decorate a test without making a method one stay out by construction, because they
/// implement neither interface: <c>RepeatAttribute</c> and <c>RetryAttribute</c> implement
/// <c>IRepeatTest</c>, <c>ValuesAttribute</c> and <c>ValueSourceAttribute</c> implement
/// <c>IParameterDataSource</c>, and <c>TestFixtureAttribute</c> implements <c>IFixtureBuilder</c> and
/// marks the class rather than the method.
/// </para>
/// <para>
/// The name-based rule is the fallback for a compilation in which the interfaces cannot be resolved by
/// their metadata name — because a name is declared more than once and therefore ambiguous, or because
/// the compilation only carries the NUnit assembly reference. Requiring the declaring assembly to belong
/// to the framework keeps a look-alike <c>ITestBuilder</c> of the project itself from being mistaken for
/// the framework's, so a recogniser left with nothing but this rule fails closed: it finds no tests
/// rather than the wrong ones.
/// </para>
/// <para>
/// <em>Counting test cases</em> is a second, independent judgement, and NUnit is by a wide margin the
/// richest case generator of the supported frameworks. Every attribute involved is matched by its simple
/// name walked up the base chain, combined with a declaring assembly that belongs to the framework, which
/// is the same rule the fallback of <see cref="IsTestMethod(IMethodSymbol)" /> applies and which covers a
/// user-written specialisation as well as the generic <c>TestCaseAttribute&lt;T&gt;</c> overloads, whose
/// simple name is <c>TestCaseAttribute</c> too.
/// </para>
/// <para>
/// The method-level attributes decide first, because they replace the parameter-level ones: NUnit's case
/// builder hands a method over to the <c>ITestBuilder</c> attributes it carries, and those supply the
/// arguments themselves. <c>TestCaseAttribute</c> contributes exactly one case each,
/// <c>TestCaseSourceAttribute</c> contributes the length of its source when that source is a sequence
/// whose elements can be counted in the syntax and a lower bound of one otherwise, and the two are summed
/// when both appear. <c>TheoryAttribute</c> is a lower bound of one without further inspection: its data
/// comes from the datapoints of the fixture, which NUnit collects from fields, properties and methods of
/// the fixture and its declaring types, and for a <see cref="bool" /> or an <see cref="Enum" /> even
/// synthesises without any datapoint at all.
/// </para>
/// <para>
/// Only when no method-level data attribute is present do the parameters decide. A method without
/// parameters is exactly one case — its inputs are hardcoded in the body, which is exactly as narrow as a
/// single row. With parameters, each one contributes the number of values its <c>IParameterDataSource</c>
/// attributes supply — <c>ValuesAttribute</c> counts its arguments, <c>ValueSourceAttribute</c> resolves
/// like a test-case source, <c>RangeAttribute</c> is computed from literal integral bounds and
/// <c>RandomAttribute</c> carries its count as the last constructor argument — and the combining strategy
/// folds them together: the cross product for <c>CombinatorialAttribute</c>, which is also the default,
/// the longest set for <c>SequentialAttribute</c>.
/// </para>
/// <para>
/// <c>PairwiseAttribute</c> is a lower bound of the longest set, never an exact number. NUnit reduces the
/// cross product to a set of cases in which every pair of values of every two parameters occurs at least
/// once, and the size of that set is the result of a generation algorithm — for two parameters it happens
/// to be the cross product, for more it is smaller in a way that depends on the algorithm and not on the
/// input sizes alone. Every value of the largest set has to appear in it, which is the bound that can be
/// stated without reproducing the algorithm.
/// </para>
/// <para>
/// <c>RepeatAttribute</c> and <c>RetryAttribute</c> deliberately do not multiply anything. Both run the
/// very same case again — <c>[Repeat]</c> unconditionally, <c>[Retry]</c> after a failure — with the very
/// same arguments, so neither adds an input combination, and an input combination is the only thing this
/// count is about. Letting them multiply would mask exactly the gap the count exists to expose: a single
/// hardcoded input repeated five times detects no more mutations than the one execution does.
/// </para>
/// </remarks>
internal sealed class NUnitTestMethodRecognizer : ITestMethodRecognizer
{
    /// <summary>
    /// The simple type name of the attribute contributing one inline case per application.
    /// </summary>
    private const string TestCaseAttributeTypeName = "TestCaseAttribute";

    /// <summary>
    /// The simple type name of the attribute contributing the cases of a referenced member.
    /// </summary>
    private const string TestCaseSourceAttributeTypeName = "TestCaseSourceAttribute";

    /// <summary>
    /// The simple type name of the attribute whose cases come from the datapoints of the fixture.
    /// </summary>
    private const string TheoryAttributeTypeName = "TheoryAttribute";

    /// <summary>
    /// The simple type name of the combining strategy taking the longest parameter value set.
    /// </summary>
    private const string SequentialAttributeTypeName = "SequentialAttribute";

    /// <summary>
    /// The simple type name of the combining strategy whose case count cannot be computed.
    /// </summary>
    private const string PairwiseAttributeTypeName = "PairwiseAttribute";

    /// <summary>
    /// The simple type name of the parameter data source listing its values as arguments.
    /// </summary>
    private const string ValuesAttributeTypeName = "ValuesAttribute";

    /// <summary>
    /// The simple type name of the parameter data source referencing a member.
    /// </summary>
    private const string ValueSourceAttributeTypeName = "ValueSourceAttribute";

    /// <summary>
    /// The simple type name of the parameter data source describing an arithmetic range.
    /// </summary>
    private const string RangeAttributeTypeName = "RangeAttribute";

    /// <summary>
    /// The simple type name of the parameter data source generating a fixed number of random values.
    /// </summary>
    private const string RandomAttributeTypeName = "RandomAttribute";

    /// <summary>
    /// The simple type name of the interface an attribute implements to supply values for one parameter.
    /// </summary>
    private const string ParameterDataSourceInterfaceTypeName = "IParameterDataSource";

    private readonly ImmutableArray<INamedTypeSymbol> _testBuilderInterfaceTypes;

    /// <summary>
    /// Initializes a new instance of the <see cref="NUnitTestMethodRecognizer" /> class.
    /// </summary>
    /// <param name="testBuilderInterfaceTypes">
    /// The resolved NUnit test-builder interface types, which may be empty when only the name-based rule
    /// is available.
    /// </param>
    public NUnitTestMethodRecognizer(ImmutableArray<INamedTypeSymbol> testBuilderInterfaceTypes) =>
        _testBuilderInterfaceTypes = testBuilderInterfaceTypes.IsDefault ? [] : testBuilderInterfaceTypes;

    /// <inheritdoc />
    public string FrameworkName => NUnitTestFrameworkProbe.Name;

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

        var attributes = method.GetAttributes();

        if (attributes.Any(attribute => IsFrameworkAttribute(attribute.AttributeClass, TheoryAttributeTypeName)))
        {
            return TestCaseCount.AtLeast(1);
        }

        var declared = GetDeclaredCaseCounts(method, attributes);

        return declared.IsEmpty
            ? GetParameterCaseCount(method, attributes)
            : declared.Aggregate((left, right) => left.Add(right));
    }

    /// <summary>
    /// Counts the cases the method-level data attributes declare: one per <c>TestCaseAttribute</c> and the
    /// resolved length per <c>TestCaseSourceAttribute</c>.
    /// </summary>
    /// <param name="method">The test method to inspect.</param>
    /// <param name="attributes">The attributes of <paramref name="method" />.</param>
    /// <returns>
    /// One count per data attribute, which is empty when the method carries none of them and the
    /// parameters therefore decide.
    /// </returns>
    private static ImmutableArray<TestCaseCount> GetDeclaredCaseCounts(
        IMethodSymbol method,
        ImmutableArray<AttributeData> attributes
    )
    {
        var builder = ImmutableArray.CreateBuilder<TestCaseCount>(attributes.Length);

        foreach (var attribute in attributes)
        {
            if (IsFrameworkAttribute(attribute.AttributeClass, TestCaseAttributeTypeName))
            {
                builder.Add(TestCaseCount.Exact(1));
            }
            else if (IsFrameworkAttribute(attribute.AttributeClass, TestCaseSourceAttributeTypeName))
            {
                builder.Add(GetSourceCaseCount(attribute, method.ContainingType));
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the case count of a <c>TestCaseSourceAttribute</c> or a <c>ValueSourceAttribute</c>, both
    /// of which name their source by a member name and an optional declaring type.
    /// </summary>
    /// <param name="attribute">The data source attribute to read.</param>
    /// <param name="containingType">The type the referenced member is looked up in by default.</param>
    /// <returns>
    /// The exact length of the referenced sequence, or a lower bound of one when the source is a type
    /// rather than a member, or when its length cannot be read off the syntax.
    /// </returns>
    private static TestCaseCount GetSourceCaseCount(AttributeData attribute, INamedTypeSymbol? containingType)
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
    /// Folds the value counts of the parameters together the way the combining strategy of
    /// <paramref name="method" /> does.
    /// </summary>
    /// <param name="method">The test method to inspect.</param>
    /// <param name="attributes">The attributes of <paramref name="method" />.</param>
    /// <returns>The number of cases the parameters produce, which is exactly one without parameters.</returns>
    private static TestCaseCount GetParameterCaseCount(IMethodSymbol method, ImmutableArray<AttributeData> attributes)
    {
        if (method.Parameters.Length == 0)
        {
            return TestCaseCount.Exact(1);
        }

        var counts = method.Parameters.Select(GetParameterValueCount).ToImmutableArray();

        if (attributes.Any(attribute => IsFrameworkAttribute(attribute.AttributeClass, PairwiseAttributeTypeName)))
        {
            return TestCaseCount.AtLeast(counts.Max(count => count.Value));
        }

        if (attributes.Any(attribute => IsFrameworkAttribute(attribute.AttributeClass, SequentialAttributeTypeName)))
        {
            return Fold(counts, counts.Max(count => count.Value));
        }

        return Fold(counts, counts.Aggregate(1L, (product, count) => Math.Min(product * count.Value, int.MaxValue)));
    }

    /// <summary>
    /// Applies <paramref name="value" /> as the folded count, exact only when every folded part is exact.
    /// </summary>
    /// <param name="counts">The folded counts.</param>
    /// <param name="value">The number the fold produced, clamped to <see cref="int.MaxValue" />.</param>
    /// <returns>The folded count.</returns>
    /// <remarks>
    /// A lower bound anywhere in <paramref name="counts" /> makes the result a lower bound as well, and it
    /// stays a sound one for both folds: the true value of every part can only be larger, and neither a
    /// product nor a maximum ever shrinks when a factor grows.
    /// </remarks>
    private static TestCaseCount Fold(ImmutableArray<TestCaseCount> counts, long value) =>
        counts.All(count => count.IsExact) ? TestCaseCount.Exact((int)value) : TestCaseCount.AtLeast((int)value);

    /// <summary>
    /// Counts the values the data sources of a single parameter supply, summed over all of them because
    /// NUnit concatenates the data of the <c>IParameterDataSource</c> attributes of one parameter.
    /// </summary>
    /// <param name="parameter">The parameter to inspect.</param>
    /// <returns>
    /// The number of values, or a lower bound of one when the parameter carries no data source at all,
    /// which is a shape NUnit builds no case from and which must therefore never be counted exactly.
    /// </returns>
    private static TestCaseCount GetParameterValueCount(IParameterSymbol parameter)
    {
        var counts = parameter
            .GetAttributes()
            .Where(attribute => IsParameterDataSource(attribute.AttributeClass))
            .Select(attribute => GetParameterValueCount(attribute, parameter.ContainingType))
            .ToImmutableArray();

        return counts.IsEmpty ? TestCaseCount.AtLeast(1) : counts.Aggregate((left, right) => left.Add(right));
    }

    /// <summary>
    /// Counts the values a single parameter data source supplies.
    /// </summary>
    /// <param name="attribute">The data source attribute to read.</param>
    /// <param name="containingType">The type a referenced source member is looked up in.</param>
    /// <returns>The number of values, or a lower bound of one for a source that cannot be computed.</returns>
    private static TestCaseCount GetParameterValueCount(AttributeData attribute, INamedTypeSymbol? containingType)
    {
        var attributeClass = attribute.AttributeClass;

        if (IsFrameworkAttribute(attributeClass, ValuesAttributeTypeName))
        {
            return GetInlineValueCount(attribute);
        }

        if (IsFrameworkAttribute(attributeClass, ValueSourceAttributeTypeName))
        {
            return GetSourceCaseCount(attribute, containingType);
        }

        if (IsFrameworkAttribute(attributeClass, RangeAttributeTypeName))
        {
            return GetRangeValueCount(attribute);
        }

        return IsFrameworkAttribute(attributeClass, RandomAttributeTypeName)
            ? GetRandomValueCount(attribute)
            : TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Counts the arguments of a <c>ValuesAttribute</c>, which are its values.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The number of values.</returns>
    /// <remarks>
    /// Up to three values bind to the individual overloads and appear as one constructor argument each;
    /// more of them bind to the <see langword="params"/> overload and appear as a single array argument. An empty
    /// <c>[Values]</c> is a lower bound of one, because NUnit then derives the values from the type of the
    /// parameter — every member of an enum, both values of a <see cref="bool" /> — which is a framework
    /// behaviour this count does not reproduce.
    /// </remarks>
    private static TestCaseCount GetInlineValueCount(AttributeData attribute)
    {
        var arguments = attribute.ConstructorArguments;

        if (arguments.Length == 1 && arguments[0].Kind == TypedConstantKind.Array)
        {
            var values = arguments[0].IsNull ? 0 : arguments[0].Values.Length;

            return values == 0 ? TestCaseCount.AtLeast(1) : TestCaseCount.Exact(values);
        }

        return arguments.Length == 0 ? TestCaseCount.AtLeast(1) : TestCaseCount.Exact(arguments.Length);
    }

    /// <summary>
    /// Computes the number of values a <c>RangeAttribute</c> produces from its literal bounds.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>
    /// The exact number of values, or a lower bound of one when the bounds are not integral, the step is
    /// zero or points away from the end, or the range does not fit an <see cref="int" />.
    /// </returns>
    /// <remarks>
    /// The range includes both bounds, so a two-argument <c>[Range(1, 3)]</c> is three values. Only the
    /// integral overloads are computed: the floating-point ones accumulate the step, so their count depends
    /// on the rounding of that accumulation rather than on arithmetic that can be reproduced here.
    /// </remarks>
    private static TestCaseCount GetRangeValueCount(AttributeData attribute)
    {
        var arguments = attribute.ConstructorArguments;

        if (arguments.Length is not (2 or 3))
        {
            return TestCaseCount.AtLeast(1);
        }

        var bounds = arguments.Select(argument => TryGetInt64(argument.Value)).ToImmutableArray();

        if (bounds.Any(bound => !bound.HasValue))
        {
            return TestCaseCount.AtLeast(1);
        }

        var from = bounds[0]!.Value;
        var to = bounds[1]!.Value;
        var implicitStep = to < from ? -1 : 1;
        var step = bounds.Length == 3 ? bounds[2]!.Value : implicitStep;

        return GetRangeValueCount(from, to, step);
    }

    /// <summary>
    /// Computes the number of values of an inclusive range.
    /// </summary>
    /// <param name="from">The first value.</param>
    /// <param name="to">The last value the range may reach.</param>
    /// <param name="step">The increment, which must point from <paramref name="from" /> towards
    /// <paramref name="to" />.</param>
    /// <returns>The exact number of values, or a lower bound of one for a range NUnit rejects.</returns>
    private static TestCaseCount GetRangeValueCount(long from, long to, long step)
    {
        if (step == 0 || (to != from && Math.Sign(to - from) != Math.Sign(step)))
        {
            return TestCaseCount.AtLeast(1);
        }

        var count = ((to - from) / step) + 1;

        return count is > 0 and <= int.MaxValue ? TestCaseCount.Exact((int)count) : TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Reads the number of values a <c>RandomAttribute</c> generates, which is its last constructor
    /// argument in every overload — the only argument of the count-only overload, and the third one of the
    /// overloads bounding the values.
    /// </summary>
    /// <param name="attribute">The attribute to read.</param>
    /// <returns>The exact number of values, or a lower bound of one when it cannot be read.</returns>
    private static TestCaseCount GetRandomValueCount(AttributeData attribute)
    {
        var arguments = attribute.ConstructorArguments;

        if (arguments.Length is not (1 or 3))
        {
            return TestCaseCount.AtLeast(1);
        }

        return arguments[arguments.Length - 1].Value is int count and > 0
            ? TestCaseCount.Exact(count)
            : TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Widens an integral attribute argument to <see cref="long" />.
    /// </summary>
    /// <param name="value">The boxed argument value.</param>
    /// <returns>
    /// The widened value, or <see langword="null" /> when the argument is not an integral one that fits.
    /// </returns>
    private static long? TryGetInt64(object? value) =>
        value switch
        {
            int number => number,
            long number => number,
            short number => number,
            sbyte number => number,
            byte number => number,
            ushort number => number,
            uint number => number,
            ulong number when number <= long.MaxValue => (long)number,
            _ => null,
        };

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
                && NUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> supplies values for a single parameter, which
    /// is what implementing the framework's <c>IParameterDataSource</c> means.
    /// </summary>
    /// <param name="attributeClass">The attribute type to classify.</param>
    /// <returns>
    /// <see langword="true" /> if the attribute is a parameter data source; otherwise
    /// <see langword="false" />.
    /// </returns>
    private static bool IsParameterDataSource(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null
        && attributeClass.AllInterfaces.Any(interfaceType =>
            string.Equals(
                interfaceType.OriginalDefinition.Name,
                ParameterDataSourceInterfaceTypeName,
                StringComparison.Ordinal
            ) && NUnitTestFrameworkProbe.IsFrameworkAssembly(interfaceType.OriginalDefinition.ContainingAssembly)
        );

    private bool IsTestAttribute(INamedTypeSymbol? attributeClass) =>
        attributeClass is not null && attributeClass.AllInterfaces.Any(IsTestBuilderInterface);

    private bool IsTestBuilderInterface(INamedTypeSymbol interfaceType)
    {
        var definition = interfaceType.OriginalDefinition;

        return IsKnownTestBuilderInterface(definition) || IsFrameworkTestBuilderInterfaceName(definition);
    }

    private bool IsKnownTestBuilderInterface(INamedTypeSymbol definition) =>
        _testBuilderInterfaceTypes.Any(interfaceType =>
            SymbolEqualityComparer.Default.Equals(definition, interfaceType)
        );

    private static bool IsFrameworkTestBuilderInterfaceName(INamedTypeSymbol definition) =>
        NUnitTestFrameworkProbe.TestBuilderInterfaceTypeNames.Contains(definition.Name, StringComparer.Ordinal)
        && NUnitTestFrameworkProbe.IsFrameworkAssembly(definition.ContainingAssembly);
}
