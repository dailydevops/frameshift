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
/// Covers the <c>StringComparison</c> operator: the exact mutation set of every one of the six members, the
/// axes the display name names, the rewritten source including its trivia, the shapes a comparison is
/// written in - an argument, a variable initialiser, a switch arm and a nullable context - and every
/// boundary the operator deliberately does not cross: an equally named enumeration from another namespace,
/// the positions that require a compile time constant, and a member imported through <c>using static</c>.
/// </summary>
public class StringComparisonMutatorTests
{
    /// <summary>
    /// The placeholder <see cref="Fixture" /> replaces by the member name.
    /// </summary>
    private const string Placeholder = "MEMBER";

    private const string ArgumentTemplate = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(left, right, StringComparison.MEMBER);
        }
        """;

    private const string VariableInitialiserSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right)
            {
                var comparison = StringComparison.Ordinal;

                return string.Equals(left, right, comparison);
            }
        }
        """;

    private const string SwitchArmSource = """
        using System;

        public class Sample
        {
            public StringComparison Select(bool ignoreCase) =>
                ignoreCase switch
                {
                    true => StringComparison.OrdinalIgnoreCase,
                    false => /*!*/StringComparison.Ordinal,
                };
        }
        """;

    private const string PatternSource = """
        using System;

        public class Sample
        {
            public bool IsOrdinal(StringComparison comparison) =>
                comparison switch
                {
                    /*!*/StringComparison.Ordinal => true,
                    _ => false,
                };
        }
        """;

    private const string CaseLabelSource = """
        using System;

        public class Sample
        {
            public int Rank(StringComparison comparison)
            {
                switch (comparison)
                {
                    case StringComparison.Ordinal:
                        return 1;
                    default:
                        return 0;
                }
            }
        }
        """;

    private const string NullableSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right, StringComparison? comparison) =>
                string.Equals(left, right, comparison ?? StringComparison.Ordinal);
        }
        """;

    private const string FullyQualifiedSource = """
        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(left, right, System.StringComparison.Ordinal);
        }
        """;

    private const string AliasSource = """
        using Comparison = System.StringComparison;

        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(left, right, Comparison.Ordinal);
        }
        """;

    private const string UsingStaticSource = """
        using static System.StringComparison;

        public class Sample
        {
            public bool Equal(string left, string right) => string.Equals(left, right, Ordinal);
        }
        """;

    private const string TriviaInsideAccessSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(left, right, StringComparison. /* pinned */ Ordinal);
        }
        """;

    private const string CommentedSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(
                    left,
                    right,
                    // ordinal is the safe default for identifiers
                    StringComparison.Ordinal
                );
        }
        """;

    private const string ReceiverSource = """
        using System;

        public class Sample
        {
            public string Describe() => StringComparison.Ordinal.ToString();
        }
        """;

    private const string UnrelatedTypeSource = """
        namespace Other
        {
            public enum StringComparison
            {
                CurrentCulture,
                CurrentCultureIgnoreCase,
                InvariantCulture,
                InvariantCultureIgnoreCase,
                Ordinal,
                OrdinalIgnoreCase,
            }

            public sealed class Sample
            {
                public StringComparison Get() => StringComparison.Ordinal;
            }
        }
        """;

    private const string AttributeSource = """
        using System;

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(StringComparison comparison) => Comparison = comparison;

            public StringComparison Comparison { get; }
        }

        public class Sample
        {
            [Marker(StringComparison.Ordinal)]
            public int Length(string text) => text.Length;
        }
        """;

    private const string ConstFieldSource = """
        using System;

        public class Sample
        {
            private const StringComparison Comparison = StringComparison.Ordinal;

            public bool Equal(string left, string right) => string.Equals(left, right, Comparison);
        }
        """;

    private const string ConstLocalSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right)
            {
                const StringComparison comparison = StringComparison.Ordinal;

                return string.Equals(left, right, comparison);
            }
        }
        """;

    private const string DefaultParameterSource = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right, StringComparison comparison = StringComparison.Ordinal)
            {
                return string.Equals(left, right, comparison);
            }
        }
        """;

    /// <summary>
    /// The six members of <c>System.StringComparison</c>, in the order the operator offers them. Every one of
    /// them has existed since .NET Framework 2.0, so every fixture below binds on net472 and on net6.0 alike.
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
    /// Every fixture of this class, because all of them are meant to be ordinary, compiling C# code - a
    /// fixture that does not compile would make the mutation asserted on it meaningless. Even the constant
    /// contexts the operator refuses are valid C# here: an enumeration member is itself a constant.
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
                    PatternSource,
                    CaseLabelSource,
                    NullableSource,
                    FullyQualifiedSource,
                    AliasSource,
                    UsingStaticSource,
                    TriviaInsideAccessSource,
                    CommentedSource,
                    ReceiverSource,
                    UnrelatedTypeSource,
                    AttributeSource,
                    ConstFieldSource,
                    ConstLocalSource,
                    DefaultParameterSource,
                }
            )
            .Select(source => (Func<string>)(() => source));

    [Test]
    [MethodDataSource(nameof(CompilingFixtures))]
    public async Task Fixture_CompilingSource_HasNoCompileError(string source)
    {
        var compilation = CompilationFactory.Create(source);

        var errors = CompilationFactory.GetCompileErrors(compilation);

        _ = await Assert.That(string.Join("; ", errors)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Metadata_Operator_DescribesTheStringComparisonFamily()
    {
        var mutator = new StringComparisonMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("culture.string-comparison");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.StringComparison);
        _ = await Assert.That(supported).IsEquivalentTo(new[] { SyntaxKind.SimpleMemberAccessExpression });
    }

    /// <summary>
    /// The full matrix: every member yields exactly the five remaining ones, in the order the enumeration
    /// declares them. The identifiers are spelled out in full, so that a renamed prefix or suffix cannot
    /// slip through unnoticed.
    /// </summary>
    /// <param name="member">The member the fixture uses.</param>
    /// <param name="expected">The operator identifiers the operator has to produce, in order.</param>
    [Test]
    [Arguments(
        "CurrentCulture",
        "culture.string-comparison.current-culture-to-current-culture-ignore-case, "
            + "culture.string-comparison.current-culture-to-invariant-culture, "
            + "culture.string-comparison.current-culture-to-invariant-culture-ignore-case, "
            + "culture.string-comparison.current-culture-to-ordinal, "
            + "culture.string-comparison.current-culture-to-ordinal-ignore-case"
    )]
    [Arguments(
        "CurrentCultureIgnoreCase",
        "culture.string-comparison.current-culture-ignore-case-to-current-culture, "
            + "culture.string-comparison.current-culture-ignore-case-to-invariant-culture, "
            + "culture.string-comparison.current-culture-ignore-case-to-invariant-culture-ignore-case, "
            + "culture.string-comparison.current-culture-ignore-case-to-ordinal, "
            + "culture.string-comparison.current-culture-ignore-case-to-ordinal-ignore-case"
    )]
    [Arguments(
        "InvariantCulture",
        "culture.string-comparison.invariant-culture-to-current-culture, "
            + "culture.string-comparison.invariant-culture-to-current-culture-ignore-case, "
            + "culture.string-comparison.invariant-culture-to-invariant-culture-ignore-case, "
            + "culture.string-comparison.invariant-culture-to-ordinal, "
            + "culture.string-comparison.invariant-culture-to-ordinal-ignore-case"
    )]
    [Arguments(
        "InvariantCultureIgnoreCase",
        "culture.string-comparison.invariant-culture-ignore-case-to-current-culture, "
            + "culture.string-comparison.invariant-culture-ignore-case-to-current-culture-ignore-case, "
            + "culture.string-comparison.invariant-culture-ignore-case-to-invariant-culture, "
            + "culture.string-comparison.invariant-culture-ignore-case-to-ordinal, "
            + "culture.string-comparison.invariant-culture-ignore-case-to-ordinal-ignore-case"
    )]
    [Arguments(
        "Ordinal",
        "culture.string-comparison.ordinal-to-current-culture, "
            + "culture.string-comparison.ordinal-to-current-culture-ignore-case, "
            + "culture.string-comparison.ordinal-to-invariant-culture, "
            + "culture.string-comparison.ordinal-to-invariant-culture-ignore-case, "
            + "culture.string-comparison.ordinal-to-ordinal-ignore-case"
    )]
    [Arguments(
        "OrdinalIgnoreCase",
        "culture.string-comparison.ordinal-ignore-case-to-current-culture, "
            + "culture.string-comparison.ordinal-ignore-case-to-current-culture-ignore-case, "
            + "culture.string-comparison.ordinal-ignore-case-to-invariant-culture, "
            + "culture.string-comparison.ordinal-ignore-case-to-invariant-culture-ignore-case, "
            + "culture.string-comparison.ordinal-ignore-case-to-ordinal"
    )]
    public async Task CreateMutations_EveryMember_ProducesTheFiveOtherMembers(string member, string expected)
    {
        var (_, mutations) = Run(Fixture(member));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo(expected);
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others(member));
            _ = await Assert.That(mutations.All(mutation => mutation.Kind == MutationKind.StringComparison)).IsTrue();
        }
    }

    /// <summary>
    /// The display name names both members and the axes the mutation moves along, so that a report tells an
    /// ordinal-versus-culture change apart from a case-sensitivity one, and both apart from a mutation that
    /// moves along both axes at once.
    /// </summary>
    /// <param name="member">The member the fixture uses.</param>
    /// <param name="target">The member the asserted mutation replaces it by.</param>
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
    public async Task CreateMutations_MemberPair_NamesTheMovedAxes(string member, string target, string axes)
    {
        var (_, mutations) = Run(Fixture(member));

        var mutation = MutationTo(mutations, target);

        _ = await Assert
            .That(mutation.DisplayName)
            .IsEqualTo($"StringComparison.{member} => StringComparison.{target} ({axes})");
    }

    [Test]
    public async Task CreateMutations_ArgumentPosition_RewritesOnlyTheMemberName()
    {
        var source = Fixture("Ordinal");
        var expected = Swap(source, "StringComparison.Ordinal", "StringComparison.CurrentCulture");
        var (tree, mutations) = Run(source);

        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
    }

    [Test]
    public async Task CreateMutations_VariableInitialiser_IsMutated()
    {
        var expected = Swap(
            VariableInitialiserSource,
            "StringComparison.Ordinal",
            "StringComparison.OrdinalIgnoreCase"
        );
        var (tree, mutations) = Run(VariableInitialiserSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "OrdinalIgnoreCase"))).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The result of a switch expression arm is an ordinary expression, unlike the pattern of the very same
    /// arm, which has to stay a constant.
    /// </summary>
    [Test]
    public async Task CreateMutations_SwitchExpressionArmResult_IsMutated()
    {
        var expected = Swap(SwitchArmSource, "/*!*/StringComparison.Ordinal", "/*!*/StringComparison.InvariantCulture");
        var (tree, mutations) = Run(SwitchArmSource, SyntaxNodeLocator.FindMarked<MemberAccessExpressionSyntax>);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "InvariantCulture"))).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_NullableContext_IsMutated()
    {
        var expected = Swap(NullableSource, "StringComparison.Ordinal", "StringComparison.CurrentCulture");
        var (tree, mutations) = Run(NullableSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_FullyQualifiedName_KeepsTheQualification()
    {
        var expected = Swap(
            FullyQualifiedSource,
            "System.StringComparison.Ordinal",
            "System.StringComparison.CurrentCulture"
        );
        var (tree, mutations) = Run(FullyQualifiedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The qualifier of a fully qualified access is itself a member access, but it names the type instead of
    /// a member of it, so nothing is offered for it.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypeQualifierOfAFullyQualifiedName_ReturnsEmpty()
    {
        var (_, mutations) = Run(FullyQualifiedSource, FindTypeQualifier);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An alias is another spelling of the very same type, so the access is mutated - and because only the
    /// member name is exchanged, the alias survives.
    /// </summary>
    [Test]
    public async Task CreateMutations_AliasedTypeName_IsMutatedAndKeepsTheAlias()
    {
        var expected = Swap(AliasSource, "Comparison.Ordinal", "Comparison.CurrentCulture");
        var (tree, mutations) = Run(AliasSource);
        var rewritten = Rewrite(tree, MutationTo(mutations, "CurrentCulture"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(rewritten).Contains("Comparison.CurrentCulture");
            _ = await Assert.That(rewritten).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// A member imported through <c>using static</c> is written as a bare identifier and is deliberately out
    /// of scope: it is no member access at all, so the syntax kind filter of the base class already refuses
    /// it. The decision is pinned here, because the alternative - offering identifiers as well - would have
    /// to prove for every identifier in a tree that it means an imported enumeration member.
    /// </summary>
    [Test]
    public async Task CreateMutations_MemberImportedByUsingStatic_ReturnsEmpty()
    {
        var mutator = new StringComparisonMutator();
        var (_, mutations) = Run(UsingStaticSource, FindImportedMember);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.SupportedSyntaxKinds.Contains(SyntaxKind.IdentifierName)).IsFalse();
            _ = await Assert.That(mutations).IsEmpty();
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(rewritten).Contains("StringComparison. /* pinned */ CurrentCulture");
            _ = await Assert.That(rewritten).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_CommentAroundTheAccess_KeepsTheTrivia()
    {
        var expected = Swap(CommentedSource, "StringComparison.Ordinal", "StringComparison.OrdinalIgnoreCase");
        var (tree, mutations) = Run(CommentedSource);

        _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "OrdinalIgnoreCase"))).IsEqualTo(expected);
    }

    /// <summary>
    /// A member read that is itself the receiver of another access is still a read of that member.
    /// </summary>
    [Test]
    public async Task CreateMutations_AccessUsedAsReceiver_IsMutated()
    {
        var expected = Swap(ReceiverSource, "StringComparison.Ordinal.", "StringComparison.CurrentCulture.");
        var (tree, mutations) = Run(ReceiverSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Targets(mutations)).IsEqualTo(Others("Ordinal"));
            _ = await Assert.That(Rewrite(tree, MutationTo(mutations, "CurrentCulture"))).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The outer access of that very fixture reads <c>ToString</c>, which is a method and therefore no member
    /// of the enumeration.
    /// </summary>
    [Test]
    public async Task CreateMutations_OtherMemberOfTheEnumeration_ReturnsEmpty()
    {
        var (_, mutations) = Run(ReceiverSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The enumeration is resolved symbolically, so a type that merely shares its name - and here even all
    /// six member names - never matches.
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

    /// <summary>
    /// The pattern of a switch arm has to stay a constant expression, so it is left alone even though the
    /// result of the very same arm is mutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConstantPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run(PatternSource, SyntaxNodeLocator.FindMarked<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CaseLabel_ReturnsEmpty()
    {
        var (_, mutations) = Run(CaseLabelSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("Ordinal"));
        var mutator = new StringComparisonMutator();
        var node = FindComparisonAccess(tree);
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

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindComparisonAccess);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new StringComparisonMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static string Ids(IEnumerable<Mutation> mutations) =>
        string.Join(", ", mutations.Select(mutation => mutation.OperatorId));

    /// <summary>
    /// The member names the mutations replace the source member by, in the produced order.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <returns>The target member names, separated by a comma.</returns>
    private static string Targets(IEnumerable<Mutation> mutations) => string.Join(", ", mutations.Select(TargetOf));

    /// <summary>
    /// The five members that are not <paramref name="member" />, in the order the operator offers them.
    /// </summary>
    /// <param name="member">The member found in the source.</param>
    /// <returns>The expected target member names, separated by a comma.</returns>
    private static string Others(string member) =>
        string.Join(", ", _memberNames.Where(name => !string.Equals(name, member, StringComparison.Ordinal)));

    private static Mutation MutationTo(Mutation[] mutations, string target) =>
        mutations.Single(mutation => string.Equals(TargetOf(mutation), target, StringComparison.Ordinal));

    private static string TargetOf(Mutation mutation) =>
        ((MemberAccessExpressionSyntax)mutation.Replacement).Name.Identifier.ValueText;

    private static SyntaxNode FindComparisonAccess(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
            tree,
            static access => _memberNames.Contains(access.Name.Identifier.ValueText, StringComparer.Ordinal)
        );

    private static SyntaxNode FindTypeQualifier(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
            tree,
            static access =>
                string.Equals(access.Name.Identifier.ValueText, "StringComparison", StringComparison.Ordinal)
        );

    private static SyntaxNode FindImportedMember(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<IdentifierNameSyntax>(
            tree,
            static name => string.Equals(name.Identifier.ValueText, "Ordinal", StringComparison.Ordinal)
        );
}
