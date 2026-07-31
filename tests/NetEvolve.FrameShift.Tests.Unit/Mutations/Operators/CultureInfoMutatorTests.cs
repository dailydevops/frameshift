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
/// Covers the culture operator: the exact mutation set of every well-known member, the rewritten source
/// including its trivia, the call shapes a culture is passed to, and every boundary the operator
/// deliberately does not cross - a named culture, an assignment to an ambient culture, a same-named type
/// from another namespace and the positions that require a compile time constant.
/// </summary>
public class CultureInfoMutatorTests
{
    private const string InvariantSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
        }
        """;

    private const string CurrentSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value) => value.ToString(CultureInfo.CurrentCulture);
        }
        """;

    private const string CurrentUiSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value) => value.ToString(CultureInfo.CurrentUICulture);
        }
        """;

    private const string FullyQualifiedSource = """
        public class Sample
        {
            public string Format(int value) =>
                value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        """;

    private const string CommentedSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value) =>
                value.ToString(
                    // the invariant culture keeps the output stable
                    CultureInfo.InvariantCulture
                );
        }
        """;

    private const string TriviaInsideAccessSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value) => value.ToString(CultureInfo. /* pinned */ InvariantCulture);
        }
        """;

    private const string ParseSource = """
        using System.Globalization;

        public class Sample
        {
            public int Parse(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        }
        """;

    private const string StringFormatSource = """
        using System.Globalization;

        public class Sample
        {
            public string Describe(int value) => string.Format(CultureInfo.InvariantCulture, "{0}", value);
        }
        """;

    private const string ConvertSource = """
        using System;
        using System.Globalization;

        public class Sample
        {
            public string? Describe(int value) => Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        """;

    private const string LambdaSource = """
        using System;
        using System.Globalization;

        public class Sample
        {
            public Func<int, string> Formatter() => value => value.ToString(CultureInfo.InvariantCulture);
        }
        """;

    private const string LocalFunctionSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value)
            {
                return Local(value);

                static string Local(int inner) => inner.ToString(CultureInfo.InvariantCulture);
            }
        }
        """;

    private const string NestedAccessSource = """
        using System.Globalization;

        public class Sample
        {
            public string Name() => CultureInfo.InvariantCulture.Name;
        }
        """;

    private const string InstanceMemberSource = """
        public class Sample
        {
            public string Name(System.Globalization.CultureInfo culture) => culture.Name;
        }
        """;

    private const string GetCultureInfoSource = """
        using System.Globalization;

        public class Sample
        {
            public CultureInfo Get() => CultureInfo.GetCultureInfo("de-DE");
        }
        """;

    private const string ObjectCreationSource = """
        using System.Globalization;

        public class Sample
        {
            public CultureInfo Get() => new CultureInfo("de-DE");
        }
        """;

    private const string AssignmentTargetSource = """
        using System.Globalization;

        public class Sample
        {
            public void Set(CultureInfo culture) => CultureInfo.CurrentCulture = culture;
        }
        """;

    private const string AssignmentSourceSource = """
        using System.Globalization;

        public class Sample
        {
            public void Reset() => CultureInfo.CurrentCulture = /*!*/CultureInfo.InvariantCulture;
        }
        """;

    private const string UnrelatedTypeSource = """
        namespace Other
        {
            public sealed class CultureInfo
            {
                public static CultureInfo InvariantCulture { get; } = new CultureInfo();
            }

            public sealed class Sample
            {
                public CultureInfo Get() => CultureInfo.InvariantCulture;
            }
        }
        """;

    private const string AttributeSource = """
        using System;
        using System.Globalization;

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(object value) => Value = value;

            public object Value { get; }
        }

        public class Sample
        {
            [Marker(CultureInfo.InvariantCulture)]
            public string Format(int value) => value.ToString();
        }
        """;

    private const string DefaultParameterSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value, CultureInfo culture = CultureInfo.InvariantCulture) =>
                value.ToString(culture);
        }
        """;

    private const string ConstFieldSource = """
        using System.Globalization;

        public class Sample
        {
            private const CultureInfo Culture = CultureInfo.InvariantCulture;
        }
        """;

    private const string CaseLabelSource = """
        using System.Globalization;

        public class Sample
        {
            public int Classify(CultureInfo culture)
            {
                switch (culture)
                {
                    case /*!*/CultureInfo.InvariantCulture:
                        return 1;
                    default:
                        return 0;
                }
            }
        }
        """;

    private const string GotoCaseSource = """
        using System.Globalization;

        public class Sample
        {
            public int Classify(int index)
            {
                switch (index)
                {
                    case 1:
                        goto case /*!*/CultureInfo.InvariantCulture;
                    default:
                        return 0;
                }
            }
        }
        """;

    private const string RelationalPatternSource = """
        using System.Globalization;

        public class Sample
        {
            public bool IsAbove(CultureInfo culture) => culture is > /*!*/CultureInfo.InvariantCulture;
        }
        """;

    private const string EnumMemberSource = """
        using System.Globalization;

        public enum Level
        {
            Low = /*!*/CultureInfo.InvariantCulture,
        }
        """;

    private const string NonConstantFieldSource = """
        using System.Globalization;

        public class Sample
        {
            private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

            public string Format(int value) => value.ToString(_culture);
        }
        """;

    private const string NonConstantLocalSource = """
        using System.Globalization;

        public class Sample
        {
            public string Format(int value)
            {
                var culture = CultureInfo.InvariantCulture;

                return value.ToString(culture);
            }
        }
        """;

    /// <summary>
    /// The fixtures that are meant to be ordinary, compiling C# code. A fixture that does not compile
    /// would make the mutation asserted on it meaningless, and the three fixtures pinning a constant
    /// context are deliberately not part of this set: a culture is a reference, so no constant position
    /// accepts one, and the very point of those fixtures is that they never reach the compiler.
    /// </summary>
    /// <returns>One factory per compiling fixture.</returns>
    public static IEnumerable<Func<string>> CompilingFixtures() =>
        new[]
        {
            InvariantSource,
            CurrentSource,
            CurrentUiSource,
            FullyQualifiedSource,
            CommentedSource,
            TriviaInsideAccessSource,
            ParseSource,
            StringFormatSource,
            ConvertSource,
            LambdaSource,
            LocalFunctionSource,
            NestedAccessSource,
            InstanceMemberSource,
            GetCultureInfoSource,
            ObjectCreationSource,
            AssignmentTargetSource,
            AssignmentSourceSource,
            UnrelatedTypeSource,
            NonConstantFieldSource,
            NonConstantLocalSource,
        }.Select(source => (Func<string>)(() => source));

    [Test]
    [MethodDataSource(nameof(CompilingFixtures))]
    public async Task Fixture_CompilingSource_HasNoCompileError(string source)
    {
        var compilation = CompilationFactory.Create(source);

        var errors = CompilationFactory.GetCompileErrors(compilation);

        _ = await Assert.That(string.Join("; ", errors)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Metadata_Operator_DescribesTheCultureFamily()
    {
        var mutator = new CultureInfoMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("culture.culture-info");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.CultureInfo);
            _ = await Assert.That(supported).IsEquivalentTo(new[] { SyntaxKind.SimpleMemberAccessExpression });
        }
    }

    /// <summary>
    /// The flagship mutation of the family, and the only one of the invariant culture: swapping it for
    /// <c>CurrentUICulture</c> as well would carry the identical signal and only duplicate a report entry.
    /// </summary>
    [Test]
    public async Task CreateMutations_InvariantCulture_ProducesTheCurrentCultureSwapOnly()
    {
        var expected = InvariantSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(InvariantSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.CultureInfo);
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("InvariantCulture => CurrentCulture");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The ambient formatting culture becomes the invariant one - which is what a build machine usually
    /// runs under anyway, so culture-sensitive code is rarely proven to be - and the resource culture,
    /// which pins the difference between formatting and resource lookup.
    /// </summary>
    [Test]
    public async Task CreateMutations_CurrentCulture_ProducesTheInvariantAndTheResourceSwap()
    {
        var expectedInvariant = CurrentSource.Replace("CurrentCulture", "InvariantCulture", StringComparison.Ordinal);
        var expectedUi = CurrentSource.Replace("CurrentCulture", "CurrentUICulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(CurrentSource);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Ids(mutations))
                .IsEqualTo("culture.culture-info.current-to-invariant, culture.culture-info.current-to-current-ui");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("CurrentCulture => InvariantCulture");
            _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("CurrentCulture => CurrentUICulture");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expectedInvariant);
            _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(expectedUi);
        }
    }

    /// <summary>
    /// The resource culture participates, but only towards the formatting culture. The invariant culture is
    /// the neutral resource fallback a lookup reaches anyway in a suite without satellite assemblies, so
    /// that mutant would survive as noise instead of as a finding.
    /// </summary>
    [Test]
    public async Task CreateMutations_CurrentUiCulture_ProducesTheCurrentCultureSwapOnly()
    {
        var expected = CurrentUiSource.Replace("CurrentUICulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(CurrentUiSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.current-ui-to-current");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("CurrentUICulture => CurrentCulture");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_FullyQualifiedName_KeepsTheQualification()
    {
        var expected = FullyQualifiedSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(FullyQualifiedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
            _ = await Assert
                .That(Rewrite(tree, mutations[0]))
                .Contains("System.Globalization.CultureInfo.CurrentCulture");
        }
    }

    [Test]
    public async Task CreateMutations_CommentAroundTheAccess_KeepsTheTrivia()
    {
        var expected = CommentedSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(CommentedSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// Only the member name is exchanged, so trivia sitting between the dot and the name survives as well.
    /// </summary>
    [Test]
    public async Task CreateMutations_CommentInsideTheAccess_KeepsTheTrivia()
    {
        var expected = TriviaInsideAccessSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(TriviaInsideAccessSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).Contains("CultureInfo. /* pinned */ CurrentCulture");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_ParseArgument_IsMutated()
    {
        var expected = ParseSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(ParseSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_StringFormatArgument_IsMutated()
    {
        var expected = StringFormatSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(StringFormatSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_ConvertToStringArgument_IsMutated()
    {
        var expected = ConvertSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(ConvertSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_InsideLambda_IsMutated()
    {
        var expected = LambdaSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(LambdaSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_InsideLocalFunction_IsMutated()
    {
        var expected = LocalFunctionSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(LocalFunctionSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// A culture read that is itself the receiver of another member access is still a read of a culture and
    /// is therefore mutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_AccessUsedAsReceiver_IsMutated()
    {
        var expected = NestedAccessSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(NestedAccessSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The outer access of the very same fixture reads <c>Name</c>, which is not a culture member.
    /// </summary>
    [Test]
    public async Task CreateMutations_OtherMemberOfTheCultureType_ReturnsEmpty()
    {
        var (_, mutations) = Run(NestedAccessSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InstanceMemberOfACulture_ReturnsEmpty()
    {
        var (_, mutations) = Run(InstanceMemberSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A culture looked up by name is not part of this operator: there is no defensible other culture to put
    /// in its place, and what carries the risk is the culture name, which is a string literal.
    /// </summary>
    [Test]
    public async Task CreateMutations_GetCultureInfoCall_ReturnsEmpty()
    {
        var (_, mutations) = Run(GetCultureInfoSource, SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A culture constructed by name is not part of this operator either, and it is not even a member access,
    /// so the syntax kind filter of the base class already refuses it.
    /// </summary>
    [Test]
    public async Task CreateMutations_CultureConstruction_ReturnsEmpty()
    {
        var mutator = new CultureInfoMutator();
        var (_, mutations) = Run(ObjectCreationSource, SyntaxNodeLocator.FindFirst<ObjectCreationExpressionSyntax>);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.SupportedSyntaxKinds.Contains(SyntaxKind.ObjectCreationExpression)).IsFalse();
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    /// <summary>
    /// Assigning to an ambient culture installs process-wide state for everything that runs afterwards
    /// instead of describing how the code under test formats, so the assignment target is left alone.
    /// </summary>
    [Test]
    public async Task CreateMutations_AssignmentTarget_ReturnsEmpty()
    {
        var (_, mutations) = Run(AssignmentTargetSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// Only the target is spared; the culture being assigned is an ordinary read and is mutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_AssignedCulture_IsMutated()
    {
        var expected = AssignmentSourceSource.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(AssignmentSourceSource, SyntaxNodeLocator.FindMarked<MemberAccessExpressionSyntax>);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The receiver type is resolved semantically, so a type that merely shares the name and the member
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
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultParameterSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantField_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstFieldSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The remaining positions that only accept a compile time constant: a <c>case</c> label, a
    /// <c>goto case</c> statement, a relational pattern and an enumeration member. A
    /// culture is an object reference and never a constant, so every one of these fixtures deliberately does
    /// not compile - they are still the only way to put a culture read in such a position, which is exactly
    /// what the operator has to skip. The symbol assertion pins that the read really binds to the culture
    /// member and that only the position kept the mutations away.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(CaseLabelSource)]
    [Arguments(GotoCaseSource)]
    [Arguments(RelationalPatternSource)]
    [Arguments(EnumMemberSource)]
    public async Task CreateMutations_PositionRequiringAConstant_ReturnsEmpty(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var access = SyntaxNodeLocator.FindMarked<MemberAccessExpressionSyntax>(tree);
        var mutator = new CultureInfoMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(access, semanticModel, CancellationToken.None)];
        var info = semanticModel.GetSymbolInfo(access);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        using (Assert.Multiple())
        {
            _ = await Assert.That(symbol?.Name).IsEqualTo("InvariantCulture");
            _ = await Assert
                .That(symbol?.ContainingType.ToDisplayString())
                .IsEqualTo("System.Globalization.CultureInfo");
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    /// <summary>
    /// A field and a local declaration without the <see langword="const" /> modifier are ordinary
    /// initializers, so the walk up the parent chain continues past them instead of treating them as a
    /// position that requires a constant.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments(NonConstantFieldSource)]
    [Arguments(NonConstantLocalSource)]
    public async Task CreateMutations_CultureInANonConstantInitializer_ProducesTheCurrentCultureSwap(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var expected = source.Replace("InvariantCulture", "CurrentCulture", StringComparison.Ordinal);
        var (tree, mutations) = Run(source);

        using (Assert.Multiple())
        {
            _ = await Assert.That(Ids(mutations)).IsEqualTo("culture.culture-info.invariant-to-current");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(InvariantSource);
        var mutator = new CultureInfoMutator();
        var node = FindCultureAccess(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindCultureAccess);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new CultureInfoMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static string Ids(IEnumerable<Mutation> mutations) =>
        string.Join(", ", mutations.Select(mutation => mutation.OperatorId));

    private static SyntaxNode FindCultureAccess(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
            tree,
            static access => IsCultureMemberName(access.Name.Identifier.ValueText)
        );

    private static bool IsCultureMemberName(string name) =>
        string.Equals(name, "InvariantCulture", StringComparison.Ordinal)
        || string.Equals(name, "CurrentCulture", StringComparison.Ordinal)
        || string.Equals(name, "CurrentUICulture", StringComparison.Ordinal);
}
