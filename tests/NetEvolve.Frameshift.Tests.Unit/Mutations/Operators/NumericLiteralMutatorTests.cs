namespace NetEvolve.Frameshift.Tests.Unit.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.Frameshift.Mutations;
using NetEvolve.Frameshift.Mutations.Operators;
using NetEvolve.Frameshift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the numeric literal operator: the neighbouring value mutations, the <c>0</c> and <c>1</c>
/// special cases, the suffix preservation, the type range guards and the constant contexts.
/// </summary>
public class NumericLiteralMutatorTests
{
    private const string AttributeSource = """
        public sealed class MarkerAttribute : System.Attribute
        {
            public MarkerAttribute(int value) => Value = value;

            public int Value { get; }
        }

        [Marker(42)]
        public class Sample { }
        """;

    private const string CaseLabelSource = """
        public class Sample
        {
            public int Get(int value)
            {
                switch (value)
                {
                    case 42:
                        return 1;
                    default:
                        return 0;
                }
            }
        }
        """;

    private const string GotoCaseSource = """
        public class Sample
        {
            public int Get(int value)
            {
                switch (value)
                {
                    case 1:
                        goto case /*!*/42;
                    case 42:
                        return 2;
                    default:
                        return 0;
                }
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_DescribesNumericLiteralFamily()
    {
        var mutator = new NumericLiteralMutator();
        SyntaxKind[] supported = [.. mutator.SupportedSyntaxKinds];

        _ = await Assert.That(mutator.Id).IsEqualTo("numeric-literal");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.NumericLiteral);
        _ = await Assert.That(supported).Count().IsEqualTo(1);
        _ = await Assert.That(supported).Contains(SyntaxKind.NumericLiteralExpression);
    }

    [Test]
    public async Task CreateMutations_IntegerLiteral_IncrementsAndDecrements()
    {
        var (tree, mutations) = Run("public class Sample { public int Get() => 5; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("5 => 6");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("5 => 4");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo("public class Sample { public int Get() => 6; }");
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo("public class Sample { public int Get() => 4; }");
    }

    [Test]
    public async Task CreateMutations_ZeroLiteral_ReturnsOnlyTheZeroToOneMutation()
    {
        var (tree, mutations) = Run("public class Sample { public int Get() => 0; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("0 => 1");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo("public class Sample { public int Get() => 1; }");
    }

    [Test]
    public async Task CreateMutations_OneLiteral_ReturnsOnlyTheOneToZeroMutation()
    {
        var (tree, mutations) = Run("public class Sample { public int Get() => 1; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.one-to-zero");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("1 => 0");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo("public class Sample { public int Get() => 0; }");
    }

    [Test]
    public async Task CreateMutations_LongLiteral_KeepsTheLiteralSuffix()
    {
        var (tree, mutations) = Run("public class Sample { public long Get() => 5L; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("5L => 6L");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("5L => 4L");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public long Get() => 6L; }");
    }

    [Test]
    public async Task CreateMutations_IntegerMaximum_SkipsTheOverflowingIncrement()
    {
        var (_, mutations) = Run("public class Sample { public int Get() => 2147483647; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("2147483647 => 2147483646");
    }

    [Test]
    public async Task CreateMutations_ByteMaximum_SkipsTheOverflowingIncrement()
    {
        var (_, mutations) = Run("public class Sample { public byte Get() => 255; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("255 => 254");
    }

    [Test]
    public async Task CreateMutations_SignedByteMaximum_SkipsTheOverflowingIncrement()
    {
        var (_, mutations) = Run("public class Sample { public sbyte Get() => 127; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("127 => 126");
    }

    [Test]
    public async Task CreateMutations_ByteWithinRange_ReturnsBothNeighbours()
    {
        var (_, mutations) = Run("public class Sample { public byte Get() => 2; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("2 => 3");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("2 => 1");
    }

    [Test]
    public async Task CreateMutations_UnsignedIntegerMaximum_SkipsTheOverflowingIncrement()
    {
        var (_, mutations) = Run("public class Sample { public uint Get() => 4294967295; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("4294967295 => 4294967294");
    }

    [Test]
    public async Task CreateMutations_DoubleLiteral_NegatesIt()
    {
        var (tree, mutations) = Run("public class Sample { public double Get() => 2.5; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.negate");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("2.5 => -2.5");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public double Get() => -2.5; }");
    }

    [Test]
    public async Task CreateMutations_DecimalLiteral_NegatesItKeepingTheSuffix()
    {
        var (_, mutations) = Run("public class Sample { public decimal Get() => 1.5m; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.negate");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("1.5m => -1.5m");
    }

    [Test]
    public async Task CreateMutations_AlreadyNegatedFloatingLiteral_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public double Get() => -2.5; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_FloatingZero_ReturnsOnlyTheZeroToOneMutation()
    {
        var (tree, mutations) = Run("public class Sample { public double Get() => 0.0; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("0 => 1");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public double Get() => 1.0; }");
    }

    [Test]
    public async Task CreateMutations_SingleZero_KeepsTheFloatSuffix()
    {
        var (tree, mutations) = Run("public class Sample { public float Get() => 0f; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public float Get() => 1f; }");
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
        var (_, mutations) = Run("public class Sample { private const int Value = 42; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ConstantLocal_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public int Get() { const int value = 42; return value; } }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DefaultParameterValue_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public int Get(int value = 42) => value; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CaseLabel_ReturnsEmpty()
    {
        var (_, mutations) = Run(CaseLabelSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_GotoCaseLabel_ReturnsEmpty()
    {
        var (_, mutations) = Run(GotoCaseSource, SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public string Get() => \"a\"; }", FindStringLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source) => Run(source, FindNumericLiteral);

    private static (SyntaxTree Tree, Mutation[] Mutations) Run(string source, Func<SyntaxTree, SyntaxNode> select)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var mutator = new NumericLiteralMutator();

        return (tree, [.. mutator.CreateMutations(select(tree), semanticModel, CancellationToken.None)]);
    }

    private static string Rewrite(SyntaxTree tree, Mutation mutation) => mutation.ApplyTo(tree).ToString();

    private static SyntaxNode FindNumericLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression)
        );

    private static SyntaxNode FindStringLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
        );
}
