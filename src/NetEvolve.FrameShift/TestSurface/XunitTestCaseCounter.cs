namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Counts the test cases an xUnit.net test method contributes. The two major versions describe their data
/// sources with the very same shapes, so the rules live here once and
/// <see cref="XunitV2TestMethodRecognizer" /> and <see cref="XunitV3TestMethodRecognizer" /> both delegate
/// to an instance built for their own version by <see cref="ForVersionTwo(IAssemblySymbol)" /> or
/// <see cref="ForVersionThree(IAssemblySymbol)" />.
/// </summary>
/// <remarks>
/// <para>
/// The counting rules are the shared ones of the test-case heuristic. An inline data attribute is one exact
/// case per occurrence, because <c>Xunit.InlineDataAttribute</c> is applicable multiple times and every
/// occurrence carries the arguments of exactly one case. Every other data source is a lower bound of one,
/// because the number of rows it yields is only known once the referenced member has been executed — which
/// an analyzer must never do. The single exception is a member data source whose referenced member is a
/// literal sequence written out in the same compilation: its length can be read off the syntax, so it is
/// exact. A method carrying inline data <em>and</em> a data source contributes the sum, exact only when
/// every part of it is exact.
/// </para>
/// <para>
/// A <c>[Fact]</c> is exactly one case. Its input values are hardcoded in the body, which is exactly as
/// narrow as a single inline data row, so it is emphatically not exempt.
/// </para>
/// <para>
/// A <c>[Theory]</c> without any data source is <em>exactly zero</em> cases, which is the one answer that
/// matches what the framework does: discovery finds no data, so it runs no case at all and reports the
/// theory as a failure — version 2 with <c>No data found for …</c>, version 3 likewise. Answering one would
/// invent a case that never runs and could make the heuristic report a mutation as reached by a single test
/// case although no test case exists; answering "at least one" would be plainly wrong, since no data
/// attribute is present and nothing at run time can add one. Zero is exact for the same reason, and it
/// aggregates correctly: added to the counts of the other tests reaching a member it changes nothing, and a
/// member reached by nothing but such a broken theory is not reported, because zero is not one.
/// </para>
/// <para>
/// A marker attribute other than the shipped <c>[Fact]</c> and <c>[Theory]</c> degrades the answer to a
/// lower bound. A custom marker may bring its own test-case discoverer and multiply the cases of a method
/// without any data attribute being involved, and version 3 ships that very shape itself:
/// <c>[CulturedFact(["en-US", "fr-FR"])]</c> is two test cases, and <c>[CulturedTheory]</c> multiplies the
/// cultures with the data rows. Claiming exactness there would let the heuristic report a mutation as
/// reached by a single case although two run. A theory-shaped marker without any data source stays exactly
/// zero even then, because multiplying no data row by any number of cultures is still no case at all.
/// </para>
/// <para>
/// The counter resolves every type it compares against inside the framework assembly it is given, never
/// through the compilation, so a project referencing both major versions at once is still judged per
/// version. Without that assembly nothing can be seen, and the answer is the lower bound of one that
/// suppresses every finding built on it — the same fail-closed direction the recognisers take.
/// </para>
/// <para>
/// Instances are immutable and therefore safe to reuse from concurrent analyzer callbacks.
/// </para>
/// </remarks>
internal sealed class XunitTestCaseCounter
{
    /// <summary>
    /// The metadata name of the shipped test attribute, identical in both major versions.
    /// </summary>
    private const string FactAttributeMetadataName = "Xunit.FactAttribute";

    /// <summary>
    /// The metadata name of the shipped theory attribute, identical in both major versions.
    /// </summary>
    private const string TheoryAttributeMetadataName = "Xunit.TheoryAttribute";

    /// <summary>
    /// The metadata name of the inline data attribute, identical in both major versions.
    /// </summary>
    private const string InlineDataAttributeMetadataName = "Xunit.InlineDataAttribute";

    /// <summary>
    /// The name of the named attribute argument that redirects a member data source to another type.
    /// </summary>
    private const string MemberTypeArgumentName = "MemberType";

    private readonly INamedTypeSymbol? _factAttributeType;
    private readonly INamedTypeSymbol? _theoryAttributeType;
    private readonly INamedTypeSymbol? _theoryMarkerInterfaceType;
    private readonly INamedTypeSymbol? _inlineDataAttributeType;
    private readonly INamedTypeSymbol? _memberDataAttributeType;
    private readonly INamedTypeSymbol? _dataAttributeType;
    private readonly INamedTypeSymbol? _dataMarkerInterfaceType;

    /// <summary>
    /// Initializes a new instance of the <see cref="XunitTestCaseCounter" /> class, resolving every type it
    /// compares against inside <paramref name="frameworkAssembly" />.
    /// </summary>
    /// <param name="frameworkAssembly">
    /// The framework assembly of one major version, or <see langword="null" /> when it is unavailable, in
    /// which case no data source can be seen and every count is a lower bound.
    /// </param>
    /// <param name="dataAttributeMetadataName">The metadata name of the base type of every data source.</param>
    /// <param name="memberDataAttributeMetadataName">
    /// The metadata name of the base type of the member data sources, the only ones that can be exact.
    /// </param>
    /// <param name="dataMarkerInterfaceMetadataName">
    /// The metadata name of the interface a data source carries, or <see langword="null" /> for a version
    /// that has none.
    /// </param>
    /// <param name="theoryMarkerInterfaceMetadataName">
    /// The metadata name of the interface a theory marker carries, or <see langword="null" /> for a version
    /// that has none.
    /// </param>
    private XunitTestCaseCounter(
        IAssemblySymbol? frameworkAssembly,
        string dataAttributeMetadataName,
        string memberDataAttributeMetadataName,
        string? dataMarkerInterfaceMetadataName,
        string? theoryMarkerInterfaceMetadataName
    )
    {
        _factAttributeType = Resolve(frameworkAssembly, FactAttributeMetadataName);
        _theoryAttributeType = Resolve(frameworkAssembly, TheoryAttributeMetadataName);
        _inlineDataAttributeType = Resolve(frameworkAssembly, InlineDataAttributeMetadataName);
        _memberDataAttributeType = Resolve(frameworkAssembly, memberDataAttributeMetadataName);
        _dataAttributeType = Resolve(frameworkAssembly, dataAttributeMetadataName);
        _dataMarkerInterfaceType = Resolve(frameworkAssembly, dataMarkerInterfaceMetadataName);
        _theoryMarkerInterfaceType = Resolve(frameworkAssembly, theoryMarkerInterfaceMetadataName);
    }

    /// <summary>
    /// Creates the counter of xUnit.net v2, whose data sources all derive from
    /// <c>Xunit.Sdk.DataAttribute</c>. Version 2 knows no marker interface, neither for a data source nor
    /// for a theory: deriving from the shipped attribute is the only way to be one.
    /// </summary>
    /// <param name="frameworkAssembly">
    /// The <c>xunit.core</c> assembly symbol, or <see langword="null" /> when it is unavailable.
    /// </param>
    /// <returns>The created counter.</returns>
    public static XunitTestCaseCounter ForVersionTwo(IAssemblySymbol? frameworkAssembly) =>
        new XunitTestCaseCounter(
            frameworkAssembly,
            "Xunit.Sdk.DataAttribute",
            "Xunit.MemberDataAttributeBase",
            dataMarkerInterfaceMetadataName: null,
            theoryMarkerInterfaceMetadataName: null
        );

    /// <summary>
    /// Creates the counter of xUnit.net v3. Version 3 moved the base types into the <c>Xunit.v3</c>
    /// namespace and added the interfaces <c>Xunit.v3.IDataAttribute</c> and
    /// <c>Xunit.v3.ITheoryAttribute</c>, which it keys on itself — an attribute may carry either without
    /// sharing a base type with the shipped ones, exactly as with the test marker.
    /// </summary>
    /// <param name="frameworkAssembly">
    /// The <c>xunit.v3.core</c> assembly symbol, or <see langword="null" /> when it is unavailable.
    /// </param>
    /// <returns>The created counter.</returns>
    public static XunitTestCaseCounter ForVersionThree(IAssemblySymbol? frameworkAssembly) =>
        new XunitTestCaseCounter(
            frameworkAssembly,
            "Xunit.v3.DataAttribute",
            "Xunit.v3.MemberDataAttributeBase",
            "Xunit.v3.IDataAttribute",
            "Xunit.v3.ITheoryAttribute"
        );

    /// <summary>
    /// Counts the test cases <paramref name="method" /> contributes.
    /// </summary>
    /// <param name="method">The test method to inspect.</param>
    /// <param name="isTestAttribute">
    /// The predicate of the calling recogniser that decides whether an attribute marks a test of its
    /// version. It is what tells a custom marker — whose discoverer may multiply the cases — from the
    /// shipped ones.
    /// </param>
    /// <returns>The number of test cases, exact or as a lower bound.</returns>
    public TestCaseCount Count(IMethodSymbol method, Func<INamedTypeSymbol?, bool> isTestAttribute)
    {
        if (_dataAttributeType is null && _dataMarkerInterfaceType is null)
        {
            return TestCaseCount.AtLeast(1);
        }

        var attributes = method.GetAttributes();
        var count = CountDataSources(attributes, method);

        if (count.Value > 0 && HasUnshippedMarker(attributes, isTestAttribute))
        {
            return TestCaseCount.AtLeast(count.Value);
        }

        return count;
    }

    /// <summary>
    /// Sums the counts of every data source on the method, falling back to the shape of a method without
    /// one: one case for a fact, no case at all for a theory whose data source is missing.
    /// </summary>
    /// <param name="attributes">The attributes of the method.</param>
    /// <param name="method">The method the attributes sit on, which member data is resolved against.</param>
    /// <returns>The counted cases.</returns>
    private TestCaseCount CountDataSources(ImmutableArray<AttributeData> attributes, IMethodSymbol method)
    {
        var total = TestCaseCount.Exact(0);
        var found = false;

        foreach (var attribute in attributes)
        {
            if (!IsDataSource(attribute.AttributeClass))
            {
                continue;
            }

            found = true;
            total = total.Add(CountDataSource(attribute, method));
        }

        if (found)
        {
            return total;
        }

        return IsTheory(attributes) ? TestCaseCount.Exact(0) : TestCaseCount.Exact(1);
    }

    /// <summary>
    /// Counts the cases of a single data source: one per inline data attribute, the literal length of a
    /// member data source that can be read off the syntax, and a lower bound of one for everything else.
    /// </summary>
    /// <param name="attribute">The data source attribute.</param>
    /// <param name="method">The method it sits on.</param>
    /// <returns>The counted cases.</returns>
    private TestCaseCount CountDataSource(AttributeData attribute, IMethodSymbol method)
    {
        if (DerivesFrom(attribute.AttributeClass, _inlineDataAttributeType))
        {
            return TestCaseCount.Exact(1);
        }

        if (DerivesFrom(attribute.AttributeClass, _memberDataAttributeType))
        {
            return CountMemberData(attribute, method);
        }

        return TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Counts the rows of a member data source by reading the literal sequence its member is initialized
    /// with. Anything else — a member of another assembly, an iterator, a computed sequence, a member that
    /// does not exist — stays the lower bound of one, because only executing it would give the answer.
    /// </summary>
    /// <param name="attribute">The member data attribute.</param>
    /// <param name="method">The method it sits on, whose type holds the member unless one is named.</param>
    /// <returns>The counted rows.</returns>
    private static TestCaseCount CountMemberData(AttributeData attribute, IMethodSymbol method)
    {
        var memberName =
            attribute.ConstructorArguments.Length == 0 ? null : attribute.ConstructorArguments[0].Value as string;

        if (string.IsNullOrEmpty(memberName))
        {
            return TestCaseCount.AtLeast(1);
        }

        var length = SequenceLengthReader.TryGetSequenceLength(
            GetMemberType(attribute) ?? method.ContainingType,
            memberName!
        );

        return length.HasValue ? TestCaseCount.Exact(length.Value) : TestCaseCount.AtLeast(1);
    }

    /// <summary>
    /// Reads the type named by the <c>MemberType</c> argument of a member data source.
    /// </summary>
    /// <param name="attribute">The member data attribute.</param>
    /// <returns>The named type, or <see langword="null" /> when the argument is absent.</returns>
    private static INamedTypeSymbol? GetMemberType(AttributeData attribute) =>
        attribute
            .NamedArguments.Where(argument =>
                string.Equals(argument.Key, MemberTypeArgumentName, StringComparison.Ordinal)
            )
            .Select(argument => argument.Value.Value as INamedTypeSymbol)
            .FirstOrDefault(type => type is not null);

    /// <summary>
    /// Determines whether the method carries a test marker that is neither the shipped
    /// <c>Xunit.FactAttribute</c> nor the shipped <c>Xunit.TheoryAttribute</c>, and whose discoverer may
    /// therefore turn the counted data into more cases than there are rows.
    /// </summary>
    /// <param name="attributes">The attributes of the method.</param>
    /// <param name="isTestAttribute">The marker predicate of the calling recogniser.</param>
    /// <returns><see langword="true" /> when such a marker is present.</returns>
    private bool HasUnshippedMarker(
        ImmutableArray<AttributeData> attributes,
        Func<INamedTypeSymbol?, bool> isTestAttribute
    ) =>
        attributes.Any(attribute =>
            isTestAttribute(attribute.AttributeClass) && !IsShippedMarker(attribute.AttributeClass)
        );

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> is one of the two shipped markers itself.
    /// </summary>
    /// <param name="attributeClass">The attribute type to judge.</param>
    /// <returns><see langword="true" /> when it is the shipped fact or theory attribute.</returns>
    private bool IsShippedMarker(INamedTypeSymbol? attributeClass) =>
        IsSameType(attributeClass, _factAttributeType) || IsSameType(attributeClass, _theoryAttributeType);

    /// <summary>
    /// Determines whether any attribute of the method marks it as a theory, by deriving from the shipped
    /// theory attribute or by carrying the theory marker interface of version 3.
    /// </summary>
    /// <param name="attributes">The attributes of the method.</param>
    /// <returns><see langword="true" /> when the method is theory-shaped.</returns>
    private bool IsTheory(ImmutableArray<AttributeData> attributes) =>
        attributes.Any(attribute =>
            DerivesFrom(attribute.AttributeClass, _theoryAttributeType)
            || Implements(attribute.AttributeClass, _theoryMarkerInterfaceType)
        );

    /// <summary>
    /// Determines whether <paramref name="attributeClass" /> is a data source of the counted version, by
    /// deriving from its data attribute base type or by carrying its data marker interface.
    /// </summary>
    /// <param name="attributeClass">The attribute type to judge.</param>
    /// <returns><see langword="true" /> when the attribute is a data source.</returns>
    private bool IsDataSource(INamedTypeSymbol? attributeClass) =>
        DerivesFrom(attributeClass, _dataAttributeType) || Implements(attributeClass, _dataMarkerInterfaceType);

    /// <summary>
    /// Determines whether <paramref name="type" /> is <paramref name="target" /> or derives from it.
    /// </summary>
    /// <param name="type">The type to walk the base chain of.</param>
    /// <param name="target">The type looked for, which may be <see langword="null" />.</param>
    /// <returns><see langword="true" /> when the target is found.</returns>
    private static bool DerivesFrom(INamedTypeSymbol? type, INamedTypeSymbol? target)
    {
        if (target is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (IsSameType(current, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="type" /> carries <paramref name="markerInterface" />, directly,
    /// through a base type or through another interface extending it.
    /// </summary>
    /// <param name="type">The type to judge.</param>
    /// <param name="markerInterface">The interface looked for, which may be <see langword="null" />.</param>
    /// <returns><see langword="true" /> when the interface is carried.</returns>
    private static bool Implements(INamedTypeSymbol? type, INamedTypeSymbol? markerInterface) =>
        markerInterface is not null
        && type is not null
        && type.AllInterfaces.Any(candidate => IsSameType(candidate, markerInterface));

    /// <summary>
    /// Compares two types by symbol identity, on their original definitions so that a generic data source
    /// such as <c>Xunit.ClassDataAttribute&lt;T&gt;</c> is recognised through its unbound definition.
    /// </summary>
    /// <param name="left">The first type, which may be <see langword="null" />.</param>
    /// <param name="right">The second type, which may be <see langword="null" />.</param>
    /// <returns><see langword="true" /> when both are the same non-null symbol.</returns>
    private static bool IsSameType(INamedTypeSymbol? left, INamedTypeSymbol? right) =>
        left is not null
        && right is not null
        && SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

    /// <summary>
    /// Resolves a type inside the framework assembly, tolerating both a missing assembly and a missing
    /// name so that a trimmed or older framework build costs exactness instead of throwing.
    /// </summary>
    /// <param name="frameworkAssembly">The assembly to resolve in, which may be <see langword="null" />.</param>
    /// <param name="metadataName">The metadata name, which may be <see langword="null" />.</param>
    /// <returns>The resolved type, or <see langword="null" />.</returns>
    private static INamedTypeSymbol? Resolve(IAssemblySymbol? frameworkAssembly, string? metadataName) =>
        frameworkAssembly is null || metadataName is null
            ? null
            : frameworkAssembly.GetTypeByMetadataName(metadataName);
}
