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
/// Covers the nullable literal operator: the mutations produced per source state for each supported
/// underlying type (<c>bool</c>, an integral and floating type, <c>char</c> and <c>Guid</c>), the
/// rewritten source, and every position it must leave alone, which is a plain non-nullable type, a
/// reference type <see langword="null" />, a constant context, and a <see langword="default" /> written
/// in place of a literal.
/// </summary>
public class NullableLiteralMutatorTests
{
    private const string TrueSource = "public class Sample { public bool? Get() => true; }";
    private const string FalseSource = "public class Sample { public bool? Get() => false; }";
    private const string NullBooleanSource = "public class Sample { public bool? Get() => null; }";

    private const string NonZeroIntSource = "public class Sample { public int? Get() => 5; }";
    private const string ZeroIntSource = "public class Sample { public int? Get() => 0; }";
    private const string NullIntSource = "public class Sample { public int? Get() => null; }";

    private const string CharSource = "public class Sample { public char? Get() => 'a'; }";
    private const string NullCharSource = "public class Sample { public char? Get() => null; }";

    private const string NullGuidSource = "public class Sample { public System.Guid? Get() => null; }";
    private const string GuidWrongNamespaceSource =
        "namespace Other { public struct Guid { } } namespace Sample { public class Get { public Other.Guid? Value() => null; } }";

    private const string PlainBooleanSource = "public class Sample { public bool Get() => true; }";
    private const string PlainIntSource = "public class Sample { public int Get() => 5; }";
    private const string ReferenceTypeNullSource = "public class Sample { public string Get() => null; }";
    private const string DefaultLiteralSource = "public class Sample { public int? Get() => default; }";
    private const string ConstantPatternSource = "public class Sample { public bool Get(int? value) => value is 5; }";
    private const string DefaultParameterSource = "public class Sample { public int? Get(int? value = 5) => value; }";
    private const string ConstFieldSource = "public class Sample { private const bool? Flag = true; }";
    private const string AttributeArgumentSource =
        "public class SampleAttribute : System.Attribute { public SampleAttribute(bool? flag) { } } [Sample(true)] public class Sample { }";
    private const string FieldInitializerSource =
        "public class Sample { private int? _value = 0; public int? Get() => _value; }";
    private const string LiftedComparisonSource = "public class Sample { public bool Get(int? value) => value == 5; }";

    private const string TriviaSource = """
        public class Sample
        {
            public int? Get() =>
                // the default-value distinction is the interesting one
                5;
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesNullableLiteralFamily()
    {
        var mutator = new NullableLiteralMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("nullable-literal");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.NullableLiteral);
            _ = await Assert.That(supported).Count().IsEqualTo(5);
            _ = await Assert.That(supported).Contains(SyntaxKind.TrueLiteralExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.FalseLiteralExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.NumericLiteralExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.CharacterLiteralExpression);
            _ = await Assert.That(supported).Contains(SyntaxKind.NullLiteralExpression);
        }
    }

    [Test]
    public async Task CreateMutations_TrueLiteral_ReplacesItByNullAndByDefault()
    {
        var (tree, mutations) = Run(TrueSource, FindBooleanOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(2);
            _ = await Assert.That(mutations[0].Kind).IsEqualTo(MutationKind.NullableLiteral);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("true => null");
            _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.NullLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullBooleanSource);

            _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("nullable-literal.literal-to-default");
            _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("true => false");
            _ = await Assert.That(mutations[1].Replacement.IsKind(SyntaxKind.FalseLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(FalseSource);
        }
    }

    /// <summary>
    /// <see langword="false" /> already is the default value of <c>bool</c>, so mutating it to that same
    /// default would not be a mutation at all; only the transition to <see langword="null" /> fires.
    /// </summary>
    [Test]
    public async Task CreateMutations_FalseLiteral_ReplacesItOnlyByNull()
    {
        var (tree, mutations) = Run(FalseSource, FindBooleanOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("false => null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullBooleanSource);
        }
    }

    /// <summary>
    /// <c>bool?</c> is the one type with a second, non-default value worth naming explicitly, so
    /// <see langword="null" /> moves to both <see langword="false" />, its default, and
    /// <see langword="true" />.
    /// </summary>
    [Test]
    public async Task CreateMutations_NullBooleanLiteral_ReplacesItByDefaultAndByTrue()
    {
        var (tree, mutations) = Run(NullBooleanSource, FindBooleanOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(2);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.null-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("null => false");
            _ = await Assert.That(mutations[0].Replacement.IsKind(SyntaxKind.FalseLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(FalseSource);

            _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("nullable-literal.null-to-true");
            _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("null => true");
            _ = await Assert.That(mutations[1].Replacement.IsKind(SyntaxKind.TrueLiteralExpression)).IsTrue();
            _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(TrueSource);
        }
    }

    [Test]
    public async Task CreateMutations_NonZeroIntLiteral_ReplacesItByNullAndByDefault()
    {
        var (tree, mutations) = Run(NonZeroIntSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(2);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("5 => null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullIntSource);

            _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("nullable-literal.literal-to-default");
            _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("5 => 0");
            _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(ZeroIntSource);
        }
    }

    /// <summary>
    /// <c>0</c> already is the default value of <c>int</c>, so only the transition to
    /// <see langword="null" /> fires.
    /// </summary>
    [Test]
    public async Task CreateMutations_ZeroIntLiteral_ReplacesItOnlyByNull()
    {
        var (tree, mutations) = Run(ZeroIntSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("0 => null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullIntSource);
        }
    }

    [Test]
    public async Task CreateMutations_NullIntLiteral_ReplacesItByDefault()
    {
        var (tree, mutations) = Run(NullIntSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.null-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("null => 0");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(ZeroIntSource);
        }
    }

    [Test]
    public async Task CreateMutations_CharLiteral_ReplacesItByNullAndByDefault()
    {
        var (tree, mutations) = Run(CharSource, FindCharOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(2);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(NullCharSource);

            _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("nullable-literal.literal-to-default");
            _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("'a' => '\\0'");
        }
    }

    [Test]
    public async Task CreateMutations_NullCharLiteral_ReplacesItByDefault()
    {
        var (_, mutations) = Run(NullCharSource, FindCharOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.null-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("null => '\\0'");
        }
    }

    /// <summary>
    /// <c>Guid</c> has no literal syntax, so <c>null</c> is the only state this operator ever meets for
    /// it; the default it moves to is written fully qualified, so the mutant compiles whether or not the
    /// mutated file has a <c>using System;</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_NullGuidLiteral_ReplacesItByDefault()
    {
        var (tree, mutations) = Run(NullGuidSource, FindBooleanOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.null-to-default");
            _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("null => Guid.Empty");
            _ = await Assert.That(Rewrite(tree, mutations[0])).Contains("global::System.Guid.Empty");
        }
    }

    /// <summary>
    /// The receiver is resolved semantically against <c>System.Guid</c> specifically, so a same-named
    /// <c>Guid</c> type declared in another namespace is never mistaken for it.
    /// </summary>
    [Test]
    public async Task CreateMutations_NullOtherNamespaceGuidLiteral_ReturnsEmpty()
    {
        var (_, mutations) = Run(GuidWrongNamespaceSource, FindBooleanOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NullableFieldInitializer_IsMutated()
    {
        var expected = FieldInitializerSource.Replace("= 0", "= null", StringComparison.Ordinal);
        var (tree, mutations) = Run(FieldInitializerSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// In <c>value == 5</c> with an <c>int?</c> operand the comparison is lifted, so the literal is
    /// converted to <c>int?</c> even though it is spelled exactly like a plain numeric literal.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedComparison_IsMutated()
    {
        var expected = LiftedComparisonSource.Replace("== 5", "== null", StringComparison.Ordinal);
        var (tree, mutations) = Run(LiftedComparisonSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsGreaterThanOrEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("nullable-literal.literal-to-null");
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    /// <summary>
    /// The replacement is a bare literal without trivia of its own, so the comment and the line breaks
    /// around the original literal have to survive the rewrite.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiteralWithTrivia_KeepsTheSurroundingTrivia()
    {
        var expected = TriviaSource.Replace("5;", "null;", StringComparison.Ordinal);
        var (tree, mutations) = Run(TriviaSource, FindNumericOrNullLiteral);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).Count().IsGreaterThanOrEqualTo(1);
            _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task CreateMutations_PlainBoolean_ReturnsEmpty()
    {
        var (_, mutations) = Run(PlainBooleanSource, FindBooleanOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_PlainInt_ReturnsEmpty()
    {
        var (_, mutations) = Run(PlainIntSource, FindNumericOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ReferenceTypeNull_ReturnsEmpty()
    {
        var (_, mutations) = Run(ReferenceTypeNullSource, FindBooleanOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A <see langword="default" /> expression is not a literal of one of the supported kinds, so the
    /// operator has nothing to offer for it even though its type is <c>int?</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_DefaultLiteral_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultLiteralSource, FindDefaultLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstantPatternSource, FindNumericOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(DefaultParameterSource, FindNumericOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstFieldInitializer_ReturnsEmpty()
    {
        var (_, mutations) = Run(ConstFieldSource, FindBooleanOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_AttributeArgument_ReturnsEmpty()
    {
        var (_, mutations) = Run(AttributeArgumentSource, FindBooleanOrNullLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public string Get() => \"x\"; }", FindStringLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableLiteralMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableLiteralMutator();
        var node = FindBooleanOrNullLiteral(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(TrueSource);
        var mutator = new NullableLiteralMutator();
        var node = FindBooleanOrNullLiteral(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NullableLiteralMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindBooleanOrNullLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal =>
                literal.IsKind(SyntaxKind.TrueLiteralExpression)
                || literal.IsKind(SyntaxKind.FalseLiteralExpression)
                || literal.IsKind(SyntaxKind.NullLiteralExpression)
        );

    private static SyntaxNode FindNumericOrNullLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal =>
                literal.IsKind(SyntaxKind.NumericLiteralExpression) || literal.IsKind(SyntaxKind.NullLiteralExpression)
        );

    private static SyntaxNode FindCharOrNullLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal =>
                literal.IsKind(SyntaxKind.CharacterLiteralExpression)
                || literal.IsKind(SyntaxKind.NullLiteralExpression)
        );

    private static SyntaxNode FindDefaultLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.DefaultLiteralExpression)
        );

    private static SyntaxNode FindStringLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
        );
}
