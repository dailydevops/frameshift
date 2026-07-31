namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the <c>StringComparer</c> operator: the exact mutation set of every one of the six well-known
/// comparers, the axes the display name names, the rewritten source including its trivia, the shapes a
/// comparer is written in - an argument, a variable initialiser, a switch arm and a nullable context - and
/// every boundary the operator deliberately does not cross: an equally named class from another namespace,
/// the positions that require a compile time constant, and a property imported through <c>using static</c>.
/// </summary>
public class StringComparerMutatorTests
{
    /// <summary>
    /// The placeholder <see cref="Fixture" /> replaces by the property name.
    /// </summary>
    private const string Placeholder = "MEMBER";

    private const string ArgumentTemplate = """
        using System;
        using System.Collections.Generic;

        public class Sample
        {
            public Dictionary<string, int> Create() =>
                new Dictionary<string, int>(StringComparer.MEMBER);
        }
        """;

    private const string VariableInitialiserSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right)
            {
                var comparer = StringComparer.Ordinal;

                return comparer.Compare(left, right);
            }
        }
        """;

    private const string SwitchArmSource = """
        using System;

        public class Sample
        {
            public StringComparer Select(bool ignoreCase) =>
                ignoreCase switch
                {
                    true => StringComparer.OrdinalIgnoreCase,
                    false => /*!*/StringComparer.Ordinal,
                };
        }
        """;

    private const string NullableSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right, StringComparer? comparer) =>
                (comparer ?? StringComparer.Ordinal).Compare(left, right);
        }
        """;

    private const string FullyQualifiedSource = """
        public class Sample
        {
            public int Compare(string left, string right) =>
                System.StringComparer.Ordinal.Compare(left, right);
        }
        """;

    private const string AliasSource = """
        using Comparer = System.StringComparer;

        public class Sample
        {
            public int Compare(string left, string right) => Comparer.Ordinal.Compare(left, right);
        }
        """;

    private const string UsingStaticSource = """
        using static System.StringComparer;

        public class Sample
        {
            public int Compare(string left, string right) => Ordinal.Compare(left, right);
        }
        """;

    private const string TriviaInsideAccessSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right) =>
                StringComparer. /* pinned */ Ordinal.Compare(left, right);
        }
        """;

    private const string CommentedSource = """
        using System;
        using System.Collections.Generic;

        public class Sample
        {
            public HashSet<string> Create() =>
                new HashSet<string>(
                    // identifiers are compared byte by byte
                    StringComparer.Ordinal
                );
        }
        """;

    private const string ReceiverSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right) => StringComparer.Ordinal.Compare(left, right);
        }
        """;

    private const string FactoryMethodSource = """
        using System;
        using System.Globalization;

        public class Sample
        {
            public StringComparer Create() => StringComparer.Create(CultureInfo.InvariantCulture, true);
        }
        """;

    private const string UnrelatedTypeSource = """
        namespace Other
        {
            public sealed class StringComparer
            {
                public static StringComparer Ordinal { get; } = new StringComparer();

                public static StringComparer OrdinalIgnoreCase { get; } = new StringComparer();
            }

            public sealed class Sample
            {
                public StringComparer Get() => StringComparer.Ordinal;
            }
        }
        """;

    private const string AttributeSource = """
        using System;

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(object comparer) => Comparer = comparer;

            public object Comparer { get; }
        }

        public class Sample
        {
            [Marker(StringComparer.Ordinal)]
            public int Length(string text) => text.Length;
        }
        """;

    private const string ConstFieldSource = """
        using System;

        public class Sample
        {
            private const StringComparer Comparer = StringComparer.Ordinal;
        }
        """;

    private const string ConstLocalSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right)
            {
                const StringComparer comparer = StringComparer.Ordinal;

                return comparer.Compare(left, right);
            }
        }
        """;

    private const string DefaultParameterSource = """
        using System;

        public class Sample
        {
            public int Compare(string left, string right, StringComparer comparer = StringComparer.Ordinal)
            {
                return comparer.Compare(left, right);
            }
        }
        """;

    /// <summary>
    /// The six well-known comparers of <c>System.StringComparer</c>, in the order the operator offers them.
    /// Every one of them has existed since .NET Framework 2.0, so every fixture below binds on net472 and on
    /// net6.0 alike.
    /// </summary>
    private static readonly string[] _memberNames =
    [
        "CurrentCulture",
        "CurrentCultureIgnoreCase",
        "InvariantCulture",
        "InvariantCultureIgnoreCase",
        "Ordinal",
        "OrdinalIgnoreCase",
    ];

    /// <summary>
    /// The fixtures that are meant to be ordinary, compiling C# code, because a fixture that does not compile
    /// would make the mutation asserted on it meaningless. The four constant contexts are deliberately not
    /// part of this set; see <see cref="ConstantContextFixtures" />.
    /// </summary>
    /// <returns>One factory per compiling fixture.</returns>
    public static IEnumerable<Func<string>> CompilingFixtures() =>
        _memberNames
            .Select(Fixture)
            .Concat(
                new[]
                {
                    VariableInitialiserSource,
                    SwitchArmSource,
                    NullableSource,
                    FullyQualifiedSource,
                    AliasSource,
                    UsingStaticSource,
                    TriviaInsideAccessSource,
                    CommentedSource,
                    ReceiverSource,
                    FactoryMethodSource,
                    UnrelatedTypeSource,
                }
            )
            .Select(source => (Func<string>)(() => source));

    /// <summary>
    /// The fixtures putting a comparer in a position that only accepts a compile time constant. A comparer is
    /// an object reference and never a constant, so none of them compiles - which is exactly the point: the
    /// operator has to stay silent on them all the same, because an analyzer sees code while it is typed.
    /// </summary>
    /// <returns>One factory per constant context fixture.</returns>
    public static IEnumerable<Func<string>> ConstantContextFixtures() =>
        new[] { AttributeSource, ConstFieldSource, ConstLocalSource, DefaultParameterSource }.Select(source =>
            (Func<string>)(() => source)
        );

    [Test]
    [MethodDataSource(nameof(CompilingFixtures))]
    public async Task Fixture_CompilingSource_HasNoCompileError(string source)
    {
        var compilation = CompilationFactory.Create(source);

        var errors = CompilationFactory.GetCompileErrors(compilation);

        _ = await Assert.That(string.Join("; ", errors)).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Pins that the constant context fixtures are not ordinary C#, so that nobody moves them into
    /// <see cref="CompilingFixtures" /> and wonders why they fail there.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    [Test]
    [MethodDataSource(nameof(ConstantContextFixtures))]
    public async Task Fixture_ConstantContextSource_DoesNotCompile(string source)
    {
        var compilation = CompilationFactory.Create(source);

        var errors = CompilationFactory.GetCompileErrors(compilation);

        _ = await Assert.That(errors.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task Metadata_Operator_DescribesTheStringComparerFamily()
    {
        var mutator = new StringComparerMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("culture.string-comparer");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.StringComparer);
        _ = await Assert.That(supported).IsEquivalentTo(new[] { SyntaxKind.SimpleMemberAccessExpression });
    }

    /// <summary>
    /// The full matrix: every comparer yields exactly the five remaining ones, in the order the operator
    /// declares them. The identifiers are spelled out in full, so that a renamed prefix or suffix cannot slip
    /// through unnoticed.
    /// </summary>
    /// <param name="member">The comparer the fixture uses.</param>
    /// <param name="expected">The operator identifiers the operator has to produce, in order.</param>
    [Test]
    [Arguments(
        "CurrentCulture",
        "culture.string-comparer.current-culture-to-current-culture-ignore-case, "
            + "culture.string-comparer.current-culture-to-invariant-culture, "
            + "culture.string-comparer.current-culture-to-invariant-culture-ignore-case, "
            + "culture.string-comparer.current-culture-to-ordinal, "
            + "culture.string-comparer.current-culture-to-ordinal-ignore-case"
    )]
    [Arguments(
        "CurrentCultureIgnoreCase",
        "culture.string-comparer.current-culture-ignore-case-to-current-culture, "
            + "culture.string-comparer.current-culture-ignore-case-to-invariant-culture, "
            + "culture.string-comparer.current-culture-ignore-case-to-invariant-culture-ignore-case, "
            + "culture.string-comparer.current-culture-ignore-case-to-ordinal, "
            + "culture.string-comparer.current-culture-ignore-case-to-ordinal-ignore-case"
    )]
    [Arguments(
        "InvariantCulture",
        "culture.string-comparer.invariant-culture-to-current-culture, "
            + "culture.string-comparer.invariant-culture-to-current-culture-ignore-case, "
            + "culture.string-comparer.invariant-culture-to-invariant-culture-ignore-case, "
            + "culture.string-comparer.invariant-culture-to-ordinal, "
            + "culture.string-comparer.invariant-culture-to-ordinal-ignore-case"
    )]
    [Arguments(
        "InvariantCultureIgnoreCase",
        "culture.string-comparer.invariant-culture-ignore-case-to-current-culture, "
            + "culture.string-comparer.invariant-culture-ignore-case-to-current-culture-ignore-case, "
            + "culture.string-comparer.invariant-culture-ignore-case-to-invariant-culture, "
            + "culture.string-comparer.invariant-culture-ignore-case-to-ordinal, "
            + "culture.string-comparer.invariant-culture-ignore-case-to-ordinal-ignore-case"
    )]
    [Arguments(
        "Ordinal",
        "culture.string-comparer.ordinal-to-current-culture, "
            + "culture.string-comparer.ordinal-to-current-culture-ignore-case, "
            + "culture.string-comparer.ordinal-to-invariant-culture, "
            + "culture.string-comparer.ordinal-to-invariant-culture-ignore-case, "
            + "culture.string-comparer.ordinal-to-ordinal-ignore-case"
    )]
    [Arguments(
        "OrdinalIgnoreCase",
        "culture.string-comparer.ordinal-ignore-case-to-current-culture, "
            + "culture.string-comparer.ordinal-ignore-case-to-current-culture-ignore-case, "
            + "culture.string-comparer.ordinal-ignore-case-to-invariant-culture, "
            + "culture.string-comparer.ordinal-ignore-case-to-invariant-culture-ignore-case, "
            + "culture.string-comparer.ordinal-ignore-case-to-ordinal"
    )]
    public async Task CreateMutations_EveryComparer_ProducesTheFiveOtherComparers(string member, string expected)
    {
        var (_, mutations) = Run(Fixture(member));

        _ = await Assert.That(Ids(mutations)).IsEqualTo(expected);
        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others(member));
        _ = await Assert.That(mutations.All(mutation => mutation.Kind == MutationKind.StringComparer)).IsTrue();
    }

    /// <summary>
    /// The display name names both comparers and the axes the mutation moves along, so that a report tells an
    /// ordinal-versus-culture change apart from a case-sensitivity one, and both apart from a mutation that
    /// moves along both axes at once.
    /// </summary>
    /// <param name="member">The comparer the fixture uses.</param>
    /// <param name="target">The comparer the asserted mutation replaces it by.</param>
    /// <param name="axes">The axes the display name has to name.</param>
    [Test]
    [Arguments("Ordinal", "OrdinalIgnoreCase", "case-sensitive => case-insensitive")]
    [Arguments("OrdinalIgnoreCase", "Ordinal", "case-insensitive => case-sensitive")]
    [Arguments("Ordinal", "InvariantCulture", "ordinal => culture")]
    [Arguments("InvariantCulture", "Ordinal", "culture => ordinal")]
    [Arguments("InvariantCulture", "CurrentCulture", "invariant culture => current culture")]
    [Arguments("CurrentCulture", "InvariantCulture", "current culture => invariant culture")]
    [Arguments("CurrentCultureIgnoreCase", "InvariantCultureIgnoreCase", "current culture => invariant culture")]
    [Arguments("Ordinal", "CurrentCultureIgnoreCase", "ordinal => culture, case-sensitive => case-insensitive")]
    [Arguments("CurrentCultureIgnoreCase", "Ordinal", "culture => ordinal, case-insensitive => case-sensitive")]
    [Arguments(
        "InvariantCultureIgnoreCase",
        "CurrentCulture",
        "invariant culture => current culture, case-insensitive => case-sensitive"
    )]
    public async Task CreateMutations_ComparerPair_NamesTheMovedAxes(string member, string target, string axes)
    {
        var (_, mutations) = Run(Fixture(member));

        var mutation = MutationTo(mutations, target);

        _ = await Assert
            .That(mutation.DisplayName)
            .IsEqualTo($"StringComparer.{member} => StringComparer.{target} ({axes})");
    }

    [Test]
    public async Task CreateMutations_ArgumentPosition_RewritesOnlyThePropertyName()
    {
        var source = Fixture("Ordinal");
        var expected = Swap(source, "StringComparer.Ordinal", "StringComparer.CurrentCulture");
        var (tree, mutations) = Run(source);

        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_VariableInitialiser_IsMutated()
    {
        var expected = Swap(VariableInitialiserSource, "StringComparer.Ordinal", "StringComparer.OrdinalIgnoreCase");
        var (tree, mutations) = Run(VariableInitialiserSource);

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "OrdinalIgnoreCase"))).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_SwitchExpressionArmResult_IsMutated()
    {
        var expected = Swap(SwitchArmSource, "/*!*/StringComparer.Ordinal", "/*!*/StringComparer.InvariantCulture");
        var (tree, mutations) = Run(SwitchArmSource, SyntaxNodeLocator.FindMarked<MemberAccessExpressionSyntax>);

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "InvariantCulture"))).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_NullableContext_IsMutated()
    {
        var expected = Swap(NullableSource, "StringComparer.Ordinal", "StringComparer.CurrentCulture");
        var (tree, mutations) = Run(NullableSource);

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_FullyQualifiedName_KeepsTheQualification()
    {
        var expected = Swap(
            FullyQualifiedSource,
            "System.StringComparer.Ordinal",
            "System.StringComparer.CurrentCulture"
        );
        var (tree, mutations) = Run(FullyQualifiedSource);

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
    }

    /// <summary>
    /// The qualifier of a fully qualified access is itself a member access, but it names the type instead of
    /// a property of it, so nothing is offered for it.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypeQualifierOfAFullyQualifiedName_ReturnsEmpty()
    {
        var (_, mutations) = Run(FullyQualifiedSource, FindTypeQualifier);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An alias is another spelling of the very same type, so the access is mutated - and because only the
    /// property name is exchanged, the alias survives.
    /// </summary>
    [Test]
    public async Task CreateMutations_AliasedTypeName_IsMutatedAndKeepsTheAlias()
    {
        var expected = Swap(AliasSource, "Comparer.Ordinal", "Comparer.CurrentCulture");
        var (tree, mutations) = Run(AliasSource);
        var rewritten = Rewrite(tree, MutationTo(mutations, "CurrentCulture"));

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(rewritten).Contains("Comparer.CurrentCulture");
        _ = await Assert.That(rewritten).IsEqualTo(expected);
    }

    /// <summary>
    /// A property imported through <c>using static</c> is written as a bare identifier and is deliberately out
    /// of scope: it is no member access at all, so the syntax kind filter of the base class already refuses
    /// it. The decision is pinned here, because the alternative - offering identifiers as well - would have to
    /// prove for every identifier in a tree that it means an imported comparer.
    /// </summary>
    [Test]
    public async Task CreateMutations_PropertyImportedByUsingStatic_ReturnsEmpty()
    {
        var mutator = new StringComparerMutator();
        var (_, mutations) = Run(UsingStaticSource, FindImportedMember);

        _ = await Assert.That(mutator.SupportedSyntaxKinds.Contains(SyntaxKind.IdentifierName)).IsFalse();
        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Only the name of the access is exchanged, so trivia sitting between the dot and the name survives.
    /// </summary>
    [Test]
    public async Task CreateMutations_CommentInsideTheAccess_KeepsTheTrivia()
    {
        var expected = Swap(TriviaInsideAccessSource, "/* pinned */ Ordinal", "/* pinned */ CurrentCulture");
        var (tree, mutations) = Run(TriviaInsideAccessSource);
        var rewritten = Rewrite(tree, MutationTo(mutations, "CurrentCulture"));

        _ = await Assert.That(rewritten).Contains("StringComparer. /* pinned */ CurrentCulture");
        _ = await Assert.That(rewritten).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_CommentAroundTheAccess_KeepsTheTrivia()
    {
        var expected = Swap(CommentedSource, "StringComparer.Ordinal", "StringComparer.OrdinalIgnoreCase");
        var (tree, mutations) = Run(CommentedSource);

        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "OrdinalIgnoreCase"))).IsEqualTo(expected);
    }

    /// <summary>
    /// A comparer read that is itself the receiver of a call is still a read of that comparer.
    /// </summary>
    [Test]
    public async Task CreateMutations_AccessUsedAsReceiver_IsMutated()
    {
        var expected = Swap(ReceiverSource, "StringComparer.Ordinal.", "StringComparer.CurrentCulture.");
        var (tree, mutations) = Run(ReceiverSource);

        _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
    }

    /// <summary>
    /// The outer access of that very fixture reads <c>Compare</c>, which is a method and therefore none of the
    /// well-known comparers.
    /// </summary>
    [Test]
    public async Task CreateMutations_InstanceMethodOfAComparer_ReturnsEmpty()
    {
        var (_, mutations) = Run(ReceiverSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A comparer built by the factory method is resolved to a method and not to a property, so it is left
    /// alone: what carries the risk there are its arguments, and they belong to other operators.
    /// </summary>
    [Test]
    public async Task CreateMutations_FactoryMethodOfTheComparerType_ReturnsEmpty()
    {
        var (_, mutations) = Run(FactoryMethodSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The declaring type is resolved symbolically, so a class that merely shares its name and its property
    /// names never matches.
    /// </summary>
    [Test]
    public async Task CreateMutations_SameNamedTypeFromAnotherNamespace_ReturnsEmpty()
    {
        var (_, mutations) = Run(UnrelatedTypeSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_AttributeArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run(AttributeSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantField_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstFieldSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantLocal_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstLocalSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultParameterSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("Ordinal"));
        var mutator = new StringComparerMutator();
        var node = FindComparerAccess(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string Fixture(string member) =>
        ArgumentTemplate.Replace(Placeholder, member, StringComparison.Ordinal);

    private static string Swap(string source, string original, string replacement) =>
        source.Replace(original, replacement, StringComparison.Ordinal);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindComparerAccess);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new StringComparerMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static string Ids(IEnumerable<Mutation> mutations) =>
        string.Join(", ", mutations.Select(mutation => mutation.OperatorId));

    /// <summary>
    /// The property names the mutations replace the source comparer by, in the produced order.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <returns>The target property names, separated by a comma.</returns>
    private static string Targets(IEnumerable<Mutation> mutations) => string.Join(", ", mutations.Select(TargetOf));

    /// <summary>
    /// The five comparers that are not <paramref name="member" />, in the order the operator offers them.
    /// </summary>
    /// <param name="member">The comparer found in the source.</param>
    /// <returns>The expected target property names, separated by a comma.</returns>
    private static string Others(string member) =>
        string.Join(", ", _memberNames.Where(name => !string.Equals(name, member, StringComparison.Ordinal)));

    private static Mutation MutationTo(Mutation[] mutations, string target) =>
        mutations.Single(mutation => string.Equals(TargetOf(mutation), target, StringComparison.Ordinal));

    private static string TargetOf(Mutation mutation) =>
        ((MemberAccessExpressionSyntax)mutation.Replacement).Name.Identifier.ValueText;

    private static SyntaxNode FindComparerAccess(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(tree, IsComparerAccess);

    private static bool IsComparerAccess(MemberAccessExpressionSyntax access) =>
        _memberNames.Contains(access.Name.Identifier.ValueText, StringComparer.Ordinal);

    private static SyntaxNode FindTypeQualifier(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
            tree,
            static access => string.Equals(access.Name.Identifier.ValueText, "StringComparer", StringComparison.Ordinal)
        );

    private static SyntaxNode FindImportedMember(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<IdentifierNameSyntax>(
            tree,
            static name => string.Equals(name.Identifier.ValueText, "Ordinal", StringComparison.Ordinal)
        );
}
