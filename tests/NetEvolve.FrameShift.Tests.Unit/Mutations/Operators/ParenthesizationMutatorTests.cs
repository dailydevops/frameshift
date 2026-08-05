namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the parenthesization (precedence) reassociation mutations, the associativity guard that
/// keeps redundant parentheses from producing a mutant, the user defined operator guard, and the
/// <see cref="ConstantContext" /> guard.
/// </summary>
public class ParenthesizationMutatorTests
{
    private const string LeftOperandSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => /*!*/(a + b) * c;
        }
        """;

    private const string RightOperandSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => a * /*!*/(b + c);
        }
        """;

    private const string SubtractDivideSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => /*!*/(a - b) / c;
        }
        """;

    private const string SameTierLeftAdditiveSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => /*!*/(a + b) + c;
        }
        """;

    private const string SameTierLeftMultiplicativeSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => /*!*/(a * b) * c;
        }
        """;

    private const string RedundantMultiplicativeInAdditiveLeftSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => /*!*/(a * b) + c;
        }
        """;

    private const string RedundantMultiplicativeInAdditiveRightSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b, int c) => a + /*!*/(b * c);
        }
        """;

    private const string NotABinaryParentSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Identity(int a) => /*!*/(a);
        }
        """;

    private const string NotABinaryInnerSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Combine(int a, int b) => /*!*/(a) * b;
        }
        """;

    private const string UserDefinedInnerOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator *(Money left, int right) => new Money(left.Amount * right);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money a, Money b, int c) => /*!*/(a + b) * c;
        }
        """;

    private const string StringOperandSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Identity(string a) => /*!*/a;
        }
        """;

    private const string DelegateOperandSource = """
        namespace Fixtures;

        internal static class Handlers
        {
            internal static System.Action Identity(System.Action a) => /*!*/a;
        }
        """;

    private const string ConstDeclarationSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static int Report()
            {
                const int a = 1;
                const int b = 2;
                const int c = 3;
                const int result = /*!*/(a + b) * c;

                return result;
            }
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Applies a surcharge before tax.
            internal static int Combine(int a, int b, int c)
            {
                /* leading */
                return /*!*/(a + b) * c; // tail
            }
        }
        """;

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ParenthesizationMutator();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo("parenthesization");
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.Parenthesization);
            _ = await Assert.That(mutator.SupportedSyntaxKinds).IsEquivalentTo([SyntaxKind.ParenthesizedExpression]);
        }
    }

    /// <summary>
    /// Acceptance: <c>(a + b) * c</c> produces a mutant equivalent to <c>a + b * c</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_AdditiveParenthesizedOnLeftOfMultiply_ProducesReassociatedMutant()
    {
        var result = Mutate(LeftOperandSource);
        var mutation = Single(result.Mutations);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("parenthesization.add-in-multiply-left");
            _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.Parenthesization);
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("(a + b) * c => a + b * c");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("a + b * c");
            _ = await Assert.That(mutation.Original).IsEqualTo(result.Node.Parent);
        }
    }

    /// <summary>
    /// Acceptance: the symmetric case, <c>a * (b + c)</c> producing <c>a * b + c</c>.
    /// </summary>
    [Test]
    public async Task CreateMutations_AdditiveParenthesizedOnRightOfMultiply_ProducesReassociatedMutant()
    {
        var result = Mutate(RightOperandSource);
        var mutation = Single(result.Mutations);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("parenthesization.add-in-multiply-right");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("a * (b + c) => a * b + c");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("a * b + c");
        }
    }

    [Test]
    public async Task CreateMutations_SubtractParenthesizedOnLeftOfDivide_ProducesReassociatedMutant()
    {
        var result = Mutate(SubtractDivideSource);
        var mutation = Single(result.Mutations);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("parenthesization.subtract-in-divide-left");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("(a - b) / c => a - b / c");
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("a - b / c");
        }
    }

    [Test]
    public async Task ApplyTo_ReassociatedMutant_KeepsSurroundingTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations);

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated).Contains("// Applies a surcharge before tax.");
            _ = await Assert.That(mutated).Contains("/* leading */");
            _ = await Assert.That(mutated).Contains("return /*!*/a + b * c; // tail");
        }
    }

    /// <summary>
    /// Acceptance: parentheses that do not change grouping, such as <c>(a + b) + c</c> and
    /// <c>(a * b) * c</c>, produce no mutation, because the left-associative reading of the operator
    /// groups identically without them.
    /// </summary>
    [Test]
    [Arguments(nameof(SameTierLeftAdditiveSource))]
    [Arguments(nameof(SameTierLeftMultiplicativeSource))]
    public async Task CreateMutations_SameTierParenthesized_ReturnsEmpty(string sourceName)
    {
        var result = Mutate(SourceByName(sourceName));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// A parenthesized multiplicative expression inside an additive one is already redundant - the
    /// multiplication already binds tighter regardless of the parentheses - so no mutation is offered
    /// for either operand position.
    /// </summary>
    [Test]
    [Arguments(nameof(RedundantMultiplicativeInAdditiveLeftSource))]
    [Arguments(nameof(RedundantMultiplicativeInAdditiveRightSource))]
    public async Task CreateMutations_MultiplicativeParenthesizedInsideAdditive_ReturnsEmpty(string sourceName)
    {
        var result = Mutate(SourceByName(sourceName));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ParentIsNotABinaryExpression_ReturnsEmpty()
    {
        var result = Mutate(NotABinaryParentSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_InnerExpressionIsNotABinaryExpression_ReturnsEmpty()
    {
        var result = Mutate(NotABinaryInnerSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// Acceptance: parentheses around a user defined operator's operand produce no mutation, because
    /// reassociating a user defined operator's precedence is out of scope.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedInnerOperator_ReturnsEmpty()
    {
        var result = Mutate(UserDefinedInnerOperatorSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// The operand guard is the same one <see cref="ArithmeticOperatorMutator" /> uses, rejecting a
    /// <see cref="string" /> operand because it means a string concatenation, not an arithmetic one.
    /// There is no compiling fixture that reaches this guard through <c>CreateMutations</c> itself: any
    /// operand of the parenthesized addition or of the enclosing multiplication that is a
    /// <see cref="string" /> makes the enclosing <c>*</c>, <c>/</c> or <c>%</c> fail to compile before
    /// the guard is ever asked, so the private guard is exercised directly instead.
    /// </summary>
    [Test]
    public async Task IsGenuineArithmeticOperand_StringOperand_ReturnsFalse()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(StringOperandSource);
        var expression = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);

        _ = await Assert.That(InvokeIsGenuineArithmeticOperand(expression, semanticModel)).IsFalse();
    }

    /// <summary>
    /// Same as <see cref="IsGenuineArithmeticOperand_StringOperand_ReturnsFalse" />, for a delegate
    /// operand, which means a delegate combination rather than an arithmetic one.
    /// </summary>
    [Test]
    public async Task IsGenuineArithmeticOperand_DelegateOperand_ReturnsFalse()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(DelegateOperandSource);
        var expression = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);

        _ = await Assert.That(InvokeIsGenuineArithmeticOperand(expression, semanticModel)).IsFalse();
    }

    /// <summary>
    /// Acceptance: the operator respects <see cref="ConstantContext.IsRequired" /> the same way the
    /// other literal/expression operators do, so a mutation inside a <see langword="const" />
    /// initializer is never offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_ConstantContext_ReturnsEmpty()
    {
        var result = Mutate(ConstDeclarationSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(LeftOperandSource);
        var node = SyntaxNodeLocator.FindMarked<ParenthesizedExpressionSyntax>(tree);
        var mutator = new ParenthesizationMutator();
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception.CancellationToken).IsEqualTo(cancellation.Token);
    }

    private static string SourceByName(string sourceName) =>
        sourceName switch
        {
            nameof(SameTierLeftAdditiveSource) => SameTierLeftAdditiveSource,
            nameof(SameTierLeftMultiplicativeSource) => SameTierLeftMultiplicativeSource,
            nameof(RedundantMultiplicativeInAdditiveLeftSource) => RedundantMultiplicativeInAdditiveLeftSource,
            nameof(RedundantMultiplicativeInAdditiveRightSource) => RedundantMultiplicativeInAdditiveRightSource,
            _ => throw new ArgumentOutOfRangeException(nameof(sourceName), sourceName, "Unknown fixture source."),
        };

    private static Mutation Single(ImmutableArray<Mutation> mutations) => mutations.Single();

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, ParenthesizedExpressionSyntax Node) Mutate(
        string source
    )
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);
        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The fixture does not compile: {DiagnosticAssertions.Describe(errors)}"
            );
        }

        var node = SyntaxNodeLocator.FindMarked<ParenthesizedExpressionSyntax>(tree);
        var mutator = new ParenthesizationMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }

    /// <summary>
    /// Invokes the private operand guard of the operator, which a compiling fixture cannot reach
    /// through <c>CreateMutations</c> itself for the reason explained on the tests that call this.
    /// </summary>
    /// <param name="expression">The operand expression to check.</param>
    /// <param name="semanticModel">The semantic model of the tree <paramref name="expression" /> belongs to.</param>
    /// <returns>Whether the operand can take part in a parenthesization mutation.</returns>
    /// <exception cref="InvalidOperationException">The guard no longer exists.</exception>
    private static bool InvokeIsGenuineArithmeticOperand(ExpressionSyntax expression, SemanticModel semanticModel)
    {
        var guard =
            typeof(ParenthesizationMutator).GetMethod(
                "IsGenuineArithmeticOperand",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException("The operand guard no longer exists.");

        return (bool)guard.Invoke(null, [expression, semanticModel, CancellationToken.None])!;
    }
}
