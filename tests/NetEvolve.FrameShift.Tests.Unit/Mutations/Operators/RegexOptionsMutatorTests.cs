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
/// Covers the <c>RegexOptions</c> operator: the exact set of produced mutations for a single flag, for
/// combinations of two and three operands and for <c>RegexOptions.None</c>, the rewritten source, the
/// three places a pattern gets its options from, and the flags and expressions the operator refuses.
/// </summary>
public class RegexOptionsMutatorTests
{
    private const string OperatorIdPrefix = "culture.regex-options.";

    private const string ConstructorSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex("^a$", /*!*/RegexOptions.IgnoreCase);
        }
        """;

    private const string StaticIsMatchSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static bool Matches(string value) => Regex.IsMatch(value, "^a$", /*!*/RegexOptions.Multiline);
        }
        """;

    private const string TwoOperandSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex("^a$", /*!*/RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
        """;

    private const string ThreeOperandSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex("^a$", /*!*/RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
        }
        """;

    private const string NoneSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex("^a$", /*!*/RegexOptions.None);
        }
        """;

    private const string NoneCombinedSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex("^a$", /*!*/RegexOptions.None | RegexOptions.IgnoreCase | RegexOptions.Multiline);
        }
        """;

    private const string CompiledSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex("^a$", /*!*/RegexOptions.Compiled);
        }
        """;

    private const string EcmaScriptSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex("^a$", /*!*/RegexOptions.ECMAScript | RegexOptions.IgnoreCase);
        }
        """;

#if NET7_0_OR_GREATER
    private const string NonBacktrackingSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex("^a$", /*!*/RegexOptions.NonBacktracking);
        }
        """;

    private const string GeneratedRegexSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static partial class Patterns
        {
            [GeneratedRegex("^a$", /*!*/RegexOptions.IgnoreCase)]
            internal static partial Regex Pattern();
        }
        """;
#endif

    private const string CustomAttributeSource = """
        namespace Fixtures;

        using System;
        using System.Text.RegularExpressions;

        [AttributeUsage(AttributeTargets.Method)]
        internal sealed class PatternAttribute : Attribute
        {
            public PatternAttribute(RegexOptions options) => Options = options;

            public RegexOptions Options { get; }
        }

        internal static class Patterns
        {
            [Pattern(/*!*/RegexOptions.IgnoreCase)]
            internal static void Match()
            {
            }
        }
        """;

    private const string ConstantFieldSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal const RegexOptions Flags = /*!*/RegexOptions.IgnoreCase;
        }
        """;

    private const string QualifiedSource = """
        namespace Fixtures;

        internal static class Patterns
        {
            internal static System.Text.RegularExpressions.Regex Create() =>
                new System.Text.RegularExpressions.Regex(
                    "^a$",
                    /*!*/System.Text.RegularExpressions.RegexOptions.IgnoreCase
                );
        }
        """;

    private const string UsingStaticSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;
        using static System.Text.RegularExpressions.RegexOptions;

        internal static class Patterns
        {
            internal static Regex Create() => new Regex("^a$", /*!*/IgnoreCase | Multiline);
        }
        """;

    private const string ParenthesizedSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex("^a$", /*!*/(RegexOptions.IgnoreCase) | RegexOptions.Multiline);
        }
        """;

    private const string VariableOperandSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create(RegexOptions extra) =>
                new Regex("^a$", /*!*/RegexOptions.IgnoreCase | extra);
        }
        """;

    private const string ForeignEnumSource = """
        namespace Fixtures;

        internal enum RegexOptions
        {
            None = 0,
            IgnoreCase = 1,
            Multiline = 2,
        }

        internal static class Patterns
        {
            internal static RegexOptions Create() => /*!*/RegexOptions.IgnoreCase | RegexOptions.Multiline;
        }
        """;

    private const string UnrelatedMemberAccessSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static bool Matches(string value) => Regex.IsMatch(value, "^a$");
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            // Creates the pattern.
            internal static Regex Create()
            {
                /* leading */
                return new Regex(
                    "^a$",
                    /*!*/RegexOptions.IgnoreCase | RegexOptions.Multiline // tail
                );
            }
        }
        """;

    private const string MultiLineCombinationSource = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create() =>
                new Regex(
                    "^a$",
                    /*!*/RegexOptions.IgnoreCase
                        | RegexOptions.Multiline
                );
        }
        """;

    private static readonly string[] _fixtures =
    [
        ConstructorSource,
        StaticIsMatchSource,
        TwoOperandSource,
        ThreeOperandSource,
        NoneSource,
        NoneCombinedSource,
        CompiledSource,
        EcmaScriptSource,
        CustomAttributeSource,
        ConstantFieldSource,
        QualifiedSource,
        UsingStaticSource,
        ParenthesizedSource,
        VariableOperandSource,
        ForeignEnumSource,
        UnrelatedMemberAccessSource,
        TriviaSource,
        MultiLineCombinationSource,
    ];

    private static readonly string[] _singleFlagSuffixes =
    [
        "remove-ignore-case",
        "add-culture-invariant",
        "add-multiline",
        "add-singleline",
        "add-explicit-capture",
        "add-ignore-pattern-whitespace",
        "add-right-to-left",
    ];

    private static readonly string[] _allAdditionSuffixes =
    [
        "add-ignore-case",
        "add-culture-invariant",
        "add-multiline",
        "add-singleline",
        "add-explicit-capture",
        "add-ignore-pattern-whitespace",
        "add-right-to-left",
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new RegexOptionsMutator();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("culture.regex-options");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.RegexOptions);
            _ = await Assert
                .That(mutator.SupportedSyntaxKinds)
                .IsEquivalentTo(new[] { SyntaxKind.SimpleMemberAccessExpression, SyntaxKind.BitwiseOrExpression });
        }
    }

    /// <summary>
    /// A fixture that does not compile makes every expectation built on it meaningless, so all of them are
    /// bound once. The <c>[GeneratedRegex]</c> fixture is deliberately not part of the list; see the test
    /// using it.
    /// </summary>
    [Test]
    public async Task Fixture_EveryFixture_Compiles()
    {
        var errors = _fixtures
            .SelectMany(source => CompilationFactory.GetCompileErrors(CompilationFactory.Create(source)))
            .Select(diagnostic => diagnostic.Id);

        _ = await Assert.That(errors).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EveryMutation_UsesTheOperatorIdPrefixAndFamilyKind()
    {
        var (_, mutations) = Mutate(TwoOperandSource);
        var offenders = mutations
            .Where(mutation => !mutation.OperatorId.StartsWith(OperatorIdPrefix, StringComparison.Ordinal))
            .Select(mutation => mutation.OperatorId);

        using (Assert.Multiple())
        {
            _ = await Assert.That(offenders).IsEmpty();
            _ = await Assert
                .That(mutations.Select(mutation => mutation.Kind).Distinct())
                .IsEquivalentTo(new[] { MutationKind.RegexOptions });
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("culture.regex-options.remove-ignore-case");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("RegexOptions - IgnoreCase");
        }
    }

    [Test]
    public async Task CreateMutations_SingleFlag_RemovesItAndAddsEveryAbsentFlag()
    {
        var (_, mutations) = Mutate(ConstructorSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert
                .That(DisplayNames(mutations))
                .IsEqualTo(
                    "RegexOptions - IgnoreCase, RegexOptions + CultureInvariant, RegexOptions + Multiline, "
                        + "RegexOptions + Singleline, RegexOptions + ExplicitCapture, "
                        + "RegexOptions + IgnorePatternWhitespace, RegexOptions + RightToLeft"
                );
        }
    }

    /// <summary>
    /// Removing the only flag of a combination leaves nothing behind, so the replacement is
    /// <c>RegexOptions.None</c> - which is also why no separate <em>to none</em> mutation exists.
    /// </summary>
    [Test]
    public async Task CreateMutations_SingleFlag_RewritesTheSource()
    {
        var (tree, mutations) = Mutate(ConstructorSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Rewrite(tree, Single(mutations, "remove-ignore-case")))
                .IsEqualTo(
                    ConstructorSource.Replace("RegexOptions.IgnoreCase", "RegexOptions.None", StringComparison.Ordinal)
                );
            _ = await Assert
                .That(Rewrite(tree, Single(mutations, "add-culture-invariant")))
                .IsEqualTo(
                    ConstructorSource.Replace(
                        "RegexOptions.IgnoreCase",
                        "RegexOptions.IgnoreCase | RegexOptions.CultureInvariant",
                        StringComparison.Ordinal
                    )
                );
        }
    }

    [Test]
    public async Task CreateMutations_StaticIsMatchArgument_IsMutated()
    {
        var (tree, mutations) = Mutate(StaticIsMatchSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Suffixes(mutations))
                .IsEqualTo(
                    "remove-multiline, add-ignore-case, add-culture-invariant, add-singleline, add-explicit-capture, "
                        + "add-ignore-pattern-whitespace, add-right-to-left"
                );
            _ = await Assert
                .That(Rewrite(tree, Single(mutations, "remove-multiline")))
                .IsEqualTo(
                    StaticIsMatchSource.Replace("RegexOptions.Multiline", "RegexOptions.None", StringComparison.Ordinal)
                );
        }
    }

    [Test]
    public async Task CreateMutations_TwoOperands_RemovesEachOperandIndividually()
    {
        var (_, mutations) = Mutate(TwoOperandSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Suffixes(mutations))
                .IsEqualTo(
                    "remove-ignore-case, remove-multiline, add-culture-invariant, add-singleline, "
                        + "add-explicit-capture, add-ignore-pattern-whitespace, add-right-to-left"
                );
            _ = await Assert.That(Replacement(mutations, "remove-ignore-case")).IsEqualTo("RegexOptions.Multiline");
            _ = await Assert.That(Replacement(mutations, "remove-multiline")).IsEqualTo("RegexOptions.IgnoreCase");
            _ = await Assert
                .That(Replacement(mutations, "add-singleline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline");
        }
    }

    [Test]
    public async Task CreateMutations_TwoOperands_EveryMutantCompiles()
    {
        var (tree, mutations) = Mutate(TwoOperandSource);
        var errors = mutations
            .Select(mutation => Rewrite(tree, mutation))
            .SelectMany(source => CompilationFactory.GetCompileErrors(CompilationFactory.Create(source)))
            .Select(diagnostic => diagnostic.Id);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(7);
            _ = await Assert.That(errors).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_ThreeOperands_RemovesEachOperandIndividually()
    {
        var (_, mutations) = Mutate(ThreeOperandSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Suffixes(mutations))
                .IsEqualTo(
                    "remove-ignore-case, remove-multiline, remove-singleline, add-culture-invariant, "
                        + "add-explicit-capture, add-ignore-pattern-whitespace, add-right-to-left"
                );
            _ = await Assert
                .That(Replacement(mutations, "remove-ignore-case"))
                .IsEqualTo("RegexOptions.Multiline | RegexOptions.Singleline");
            _ = await Assert
                .That(Replacement(mutations, "remove-multiline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Singleline");
            _ = await Assert
                .That(Replacement(mutations, "remove-singleline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Multiline");
            _ = await Assert
                .That(Replacement(mutations, "add-culture-invariant"))
                .IsEqualTo(
                    "RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline "
                        + "| RegexOptions.CultureInvariant"
                );
        }
    }

    /// <summary>
    /// <c>RegexOptions.None</c> carries no bit, so there is nothing to remove and an added flag replaces
    /// the <c>None</c> instead of being combined with it.
    /// </summary>
    [Test]
    public async Task CreateMutations_None_AddsEveryFlagAndRemovesNothing()
    {
        var (tree, mutations) = Mutate(NoneSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_allAdditionSuffixes));
            _ = await Assert.That(Replacement(mutations, "add-ignore-case")).IsEqualTo("RegexOptions.IgnoreCase");
            _ = await Assert.That(Replacement(mutations, "add-right-to-left")).IsEqualTo("RegexOptions.RightToLeft");
            _ = await Assert
                .That(Rewrite(tree, Single(mutations, "add-ignore-case")))
                .IsEqualTo(
                    NoneSource.Replace("RegexOptions.None", "RegexOptions.IgnoreCase", StringComparison.Ordinal)
                );
        }
    }

    /// <summary>
    /// A <c>None</c> operand next to real flags is redundant and is dropped from every rebuilt combination,
    /// so it neither shows up in a replacement nor is offered for removal.
    /// </summary>
    [Test]
    public async Task CreateMutations_NoneCombinedWithFlags_DropsTheRedundantNone()
    {
        var (_, mutations) = Mutate(NoneCombinedSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Suffixes(mutations))
                .IsEqualTo(
                    "remove-ignore-case, remove-multiline, add-culture-invariant, add-singleline, "
                        + "add-explicit-capture, add-ignore-pattern-whitespace, add-right-to-left"
                );
            _ = await Assert.That(Replacement(mutations, "remove-ignore-case")).IsEqualTo("RegexOptions.Multiline");
            _ = await Assert
                .That(Replacement(mutations, "add-singleline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline");
        }
    }

    /// <summary>
    /// <c>Compiled</c> only decides how the engine is built, so adding or removing it can never change a
    /// match result. It is neither offered nor dropped: the flag survives every replacement unchanged.
    /// </summary>
    [Test]
    public async Task CreateMutations_CompiledFlag_IsNeitherAddedNorRemoved()
    {
        var (_, mutations) = Mutate(CompiledSource);
        var offenders = Mentioning(mutations, "Compiled");

        using (Assert.Multiple())
        {
            _ = await Assert.That(offenders).IsEmpty();
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_allAdditionSuffixes));
            _ = await Assert
                .That(Replacement(mutations, "add-ignore-case"))
                .IsEqualTo("RegexOptions.Compiled | RegexOptions.IgnoreCase");
        }
    }

    /// <summary>
    /// <c>ECMAScript</c> is legal only together with a small set of other flags, so a mutant adding it
    /// would make the <c>Regex</c> constructor throw instead of matching differently. It stays out of the
    /// offered set and, like every other flag outside it, is carried over unchanged.
    /// </summary>
    [Test]
    public async Task CreateMutations_EcmaScriptFlag_IsNeitherAddedNorRemoved()
    {
        var (_, mutations) = Mutate(EcmaScriptSource);
        var offenders = Mentioning(mutations, "ECMAScript");

        using (Assert.Multiple())
        {
            _ = await Assert.That(offenders).IsEmpty();
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert.That(Replacement(mutations, "remove-ignore-case")).IsEqualTo("RegexOptions.ECMAScript");
            _ = await Assert
                .That(Replacement(mutations, "add-multiline"))
                .IsEqualTo("RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.Multiline");
        }
    }

#if NET7_0_OR_GREATER
    /// <summary>
    /// <c>NonBacktracking</c> decides which constructs a pattern may use at all - a lookaround or a
    /// backreference throws with it - so a mutant carrying it fails for a reason that has nothing to do
    /// with the behaviour under test. It is neither offered nor dropped.
    /// </summary>
    /// <remarks>
    /// The fixture is compiled against the reference assemblies of the framework the test run itself
    /// executes on, and the flag was introduced with .NET 7. On net6.0 and the classic frameworks the
    /// member does not exist, and a fixture naming it would not bind at all, which would make the
    /// expectation hold for the wrong reason.
    /// </remarks>
    [Test]
    public async Task CreateMutations_NonBacktrackingFlag_IsNeitherAddedNorRemoved()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(NonBacktrackingSource);
        var (_, mutations) = Mutate(NonBacktrackingSource);
        var offenders = Mentioning(mutations, "NonBacktracking");

        using (Assert.Multiple())
        {
            _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
            _ = await Assert.That(offenders).IsEmpty();
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_allAdditionSuffixes));
            _ = await Assert
                .That(Replacement(mutations, "add-ignore-case"))
                .IsEqualTo("RegexOptions.NonBacktracking | RegexOptions.IgnoreCase");
        }
    }

    /// <summary>
    /// The <c>RegexOptions</c> argument of <c>[GeneratedRegex]</c> is an attribute argument and therefore a
    /// compile time constant, yet it is mutated: unlike a <see langword="const" /> whose value is itself the
    /// observed thing, this constant is the input of a matcher whose behaviour is observed at run time. The
    /// replacement is a combination of enum members and hence constant as well, so the mutant is legal.
    /// </summary>
    /// <remarks>
    /// The fixture deliberately uses the real partial declaration a consumer writes. Without the regex
    /// source generator running there is no implementing part, so the fixture does not compile - which
    /// does not matter here, because binding the attribute argument does not depend on it.
    /// </remarks>
    [Test]
    public async Task CreateMutations_GeneratedRegexAttributeArgument_IsMutated()
    {
        var (tree, mutations) = Mutate(GeneratedRegexSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert
                .That(Rewrite(tree, Single(mutations, "add-culture-invariant")))
                .IsEqualTo(
                    GeneratedRegexSource.Replace(
                        "RegexOptions.IgnoreCase",
                        "RegexOptions.IgnoreCase | RegexOptions.CultureInvariant",
                        StringComparison.Ordinal
                    )
                );
        }
    }
#endif

    /// <summary>
    /// The same decision as for <c>[GeneratedRegex]</c>, pinned on every target framework with an
    /// attribute the fixture declares itself.
    /// </summary>
    [Test]
    public async Task CreateMutations_AttributeArgument_IsMutated()
    {
        var (_, mutations) = Mutate(CustomAttributeSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert
                .That(Replacement(mutations, "add-multiline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Multiline");
        }
    }

    /// <summary>
    /// A <see langword="const" /> initializer is mutated too, because a combination of enum members is
    /// itself a constant expression and therefore legal in exactly the same position.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConstantFieldInitializer_IsMutated()
    {
        var (tree, mutations) = Mutate(ConstantFieldSource);
        var mutated = Rewrite(tree, Single(mutations, "add-multiline"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert.That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutated))).IsEmpty();
        }
    }

    /// <summary>
    /// Added members follow the spelling of the operands already there, so a fully qualified fixture stays
    /// fully qualified and the mutant binds without a using directive.
    /// </summary>
    [Test]
    public async Task CreateMutations_FullyQualifiedFlag_KeepsTheQualifier()
    {
        var (_, mutations) = Mutate(QualifiedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Suffixes(mutations)).IsEqualTo(Join(_singleFlagSuffixes));
            _ = await Assert
                .That(Replacement(mutations, "remove-ignore-case"))
                .IsEqualTo("System.Text.RegularExpressions.RegexOptions.None");
            _ = await Assert
                .That(Replacement(mutations, "add-multiline"))
                .IsEqualTo(
                    "System.Text.RegularExpressions.RegexOptions.IgnoreCase "
                        + "| System.Text.RegularExpressions.RegexOptions.Multiline"
                );
        }
    }

    /// <summary>
    /// The qualifier of a fully qualified member is itself a member access whose type is the enum, but it
    /// denotes the type and not a value, so it is not a flag expression.
    /// </summary>
    [Test]
    public async Task CreateMutations_TypeQualifierOfAQualifiedFlag_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(
            QualifiedSource,
            static tree =>
                SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
                    tree,
                    static memberAccess =>
                        string.Equals(memberAccess.Name.Identifier.ValueText, "RegexOptions", StringComparison.Ordinal)
                )
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// With <c>using static</c> the members are written as bare names; added members follow that spelling.
    /// </summary>
    [Test]
    public async Task CreateMutations_BareFlagNames_KeepTheBareSpelling()
    {
        var (_, mutations) = Mutate(UsingStaticSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Suffixes(mutations))
                .IsEqualTo(
                    "remove-ignore-case, remove-multiline, add-culture-invariant, add-singleline, "
                        + "add-explicit-capture, add-ignore-pattern-whitespace, add-right-to-left"
                );
            _ = await Assert.That(Replacement(mutations, "remove-ignore-case")).IsEqualTo("Multiline");
            _ = await Assert
                .That(Replacement(mutations, "add-singleline"))
                .IsEqualTo("IgnoreCase | Multiline | Singleline");
        }
    }

    /// <summary>
    /// Redundant parentheses around an operand carry no meaning for a flag combination and are dropped by
    /// the rebuild.
    /// </summary>
    [Test]
    public async Task CreateMutations_ParenthesizedOperand_IsTreatedLikeTheBareOperand()
    {
        var (_, mutations) = Mutate(ParenthesizedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Replacement(mutations, "remove-multiline")).IsEqualTo("RegexOptions.IgnoreCase");
            _ = await Assert
                .That(Replacement(mutations, "add-singleline"))
                .IsEqualTo("RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline");
        }
    }

    /// <summary>
    /// Only the outermost expression of a combination is mutated. An operand of a wider combination is
    /// skipped, so that the same combination is not offered twice with overlapping replacements.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperandOfAWiderCombination_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(TwoOperandSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An operand whose value is only known at run time cannot be reasoned about, so the whole combination
    /// is left alone rather than mutated on a guess.
    /// </summary>
    [Test]
    public async Task CreateMutations_NonConstantOperand_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(VariableOperandSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The enum is resolved through the compilation, so an enum of another namespace that happens to carry
    /// the same name and the same member names is not touched.
    /// </summary>
    [Test]
    public async Task CreateMutations_SameNamedEnumOfAnotherNamespace_ReturnsEmpty()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(ForeignEnumSource);
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new RegexOptionsMutator();
        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToArray();
        var bound = semanticModel.GetTypeInfo(node).Type;

        using (Assert.Multiple())
        {
            _ = await Assert.That(bound?.ToDisplayString()).IsEqualTo("Fixtures.RegexOptions");
            _ = await Assert
                .That(compilation.GetTypeByMetadataName("System.Text.RegularExpressions.RegexOptions"))
                .IsNotNull();
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_MemberAccessOfAnotherType_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(
            UnrelatedMemberAccessSource,
            SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Mutate(
            UnrelatedMemberAccessSource,
            SyntaxNodeLocator.FindFirst<InvocationExpressionSyntax>
        );

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The trivia around the whole expression - the leading comment and the trailing end of line comment -
    /// belongs to the replaced node and is restored on the replacement.
    /// </summary>
    [Test]
    public async Task CreateMutations_SurroundingTrivia_IsPreserved()
    {
        var (tree, mutations) = Mutate(TriviaSource);

        _ = await Assert
            .That(Rewrite(tree, Single(mutations, "remove-multiline")))
            .IsEqualTo(
                TriviaSource.Replace(
                    "RegexOptions.IgnoreCase | RegexOptions.Multiline",
                    "RegexOptions.IgnoreCase",
                    StringComparison.Ordinal
                )
            );
    }

    /// <summary>
    /// A combination is rebuilt from its operands and joined by <c>|</c> with a single space, so a
    /// combination spread over several lines is collapsed onto one. Only the trivia inside the expression
    /// is affected; everything around it stays.
    /// </summary>
    [Test]
    public async Task CreateMutations_MultiLineCombination_IsRebuiltOnASingleLine()
    {
        var collapsed = "RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline";
        var (tree, mutations) = Mutate(MultiLineCombinationSource);
        var mutated = Rewrite(tree, Single(mutations, "add-singleline"));

        using (Assert.Multiple())
        {
            _ = await Assert.That(Replacement(mutations, "add-singleline")).IsEqualTo(collapsed);
            _ = await Assert.That(Mentions(mutated, "/*!*/" + collapsed)).IsTrue();
            _ = await Assert.That(Mentions(mutated, "internal static Regex Create() =>")).IsTrue();
            _ = await Assert.That(CompilationFactory.GetCompileErrors(CompilationFactory.Create(mutated))).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(ConstructorSource);
        var mutator = new RegexOptionsMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(ConstructorSource);
        var mutator = new RegexOptionsMutator();
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TwoOperandSource);
        var mutator = new RegexOptionsMutator();
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source) =>
        Mutate(source, SyntaxNodeLocator.FindMarked<ExpressionSyntax>);

    private static (SyntaxTree Tree, Mutation[] Mutations) Mutate(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new RegexOptionsMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    /// <summary>
    /// Selects the mutations whose display name names <paramref name="flagName" />, which is how the tests pin
    /// that an excluded flag is never added and never removed.
    /// </summary>
    /// <param name="mutations">The produced mutations.</param>
    /// <param name="flagName">The flag name that must not appear.</param>
    /// <returns>The offending mutations, expected to be none.</returns>
    private static IEnumerable<string> Mentioning(Mutation[] mutations, string flagName) =>
        mutations.Where(mutation => Mentions(mutation.DisplayName, flagName)).Select(mutation => mutation.OperatorId);

    /// <summary>
    /// Ordinal substring test. The <c>StringComparison</c> overload of <c>Contains</c> reaches the classic
    /// targets through the <c>Polyfill</c> package this project references.
    /// </summary>
    /// <param name="value">The string to search.</param>
    /// <param name="part">The substring to look for.</param>
    /// <returns><see langword="true" /> when the substring occurs.</returns>
    private static bool Mentions(string value, string part) => value.Contains(part, StringComparison.Ordinal);

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static Mutation Single(Mutation[] mutations, string suffix) =>
        mutations.Single(mutation =>
            string.Equals(mutation.OperatorId, OperatorIdPrefix + suffix, StringComparison.Ordinal)
        );

    private static string Replacement(Mutation[] mutations, string suffix) =>
        Single(mutations, suffix).Replacement.ToString();

    private static string Suffixes(Mutation[] mutations) =>
        Join(mutations.Select(mutation => mutation.OperatorId.Substring(OperatorIdPrefix.Length)));

    private static string DisplayNames(Mutation[] mutations) =>
        Join(mutations.Select(mutation => mutation.DisplayName));

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
