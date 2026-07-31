namespace NetEvolve.Frameshift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Reflection;
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
/// Covers the binary arithmetic operator mutations, the operand guards that keep string
/// concatenations and delegate combinations out, and the user defined operator handling.
/// </summary>
public class ArithmeticOperatorMutatorTests
{
    private const string StringOperandSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Combine(string left, string right) => /*!*/left + right;
        }
        """;

    private const string StringLiteralSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Combine() => /*!*/"a" + "b";
        }
        """;

    private const string DelegateOperandSource = """
        namespace Fixtures;

        internal static class Handlers
        {
            internal static System.Action Combine(System.Action left, System.Action right) => /*!*/left + right;
        }
        """;

    private const string AddOnlyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string AddAndSubtractOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string AllOperatorsSource = """
        namespace Fixtures;

        internal sealed class Vector
        {
            internal Vector(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Vector operator +(Vector left, Vector right) => new Vector(left.Amount + right.Amount);

            public static Vector operator -(Vector left, Vector right) => new Vector(left.Amount - right.Amount);

            public static Vector operator *(Vector left, Vector right) => new Vector(left.Amount * right.Amount);

            public static Vector operator /(Vector left, Vector right) => new Vector(left.Amount / right.Amount);

            public static Vector operator %(Vector left, Vector right) => new Vector(left.Amount % right.Amount);
        }

        internal static class Vectors
        {
            internal static Vector Combine(Vector left, Vector right) => /*!*/left + right;
        }
        """;

    private const string GenericOperatorSource = """
        namespace Fixtures;

        internal readonly struct Box<TValue>
        {
            internal Box(TValue value) => Value = value;

            internal TValue Value { get; }

            public static Box<TValue> operator +(Box<TValue> left, Box<TValue> right) => left;

            public static Box<TValue> operator *(Box<TValue> left, Box<TValue> right) => right;
        }

        internal static class Boxes
        {
            internal static Box<int> Combine(Box<int> left, Box<int> right) => /*!*/left + right;
        }
        """;

    private const string NullableLiftedOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }

        internal static class Wallet
        {
            internal static Money? Combine(Money? left, Money? right) => /*!*/left + right;
        }
        """;

    private const string ImplicitConversionOperatorSource = """
        namespace Fixtures;

        internal readonly struct Cents
        {
            internal Cents(int amount) => Amount = amount;

            internal int Amount { get; }

            public static implicit operator Money(Cents value) => new Money(value.Amount);
        }

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Cents right) => /*!*/left + right;
        }
        """;

    private const string ImplicitStringConversionSource = """
        namespace Fixtures;

        internal readonly struct Slug
        {
            internal Slug(string value) => Value = value;

            internal string Value { get; }

            public static implicit operator string(Slug value) => value.Value;
        }

        internal static class Slugs
        {
            internal static string Combine(Slug left, Slug right) => /*!*/left + right;
        }
        """;

    private const string MismatchedResultTypeOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static int operator +(Money left, Money right) => left.Amount + right.Amount;

            public static string operator -(Money left, Money right) => "money";
        }

        internal static class Wallet
        {
            internal static int Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string CheckedOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator checked +(Money left, Money right) => new Money(0);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);

            public static Money operator checked -(Money left, Money right) => new Money(0);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => checked(/*!*/left + right);
        }
        """;

    private const string ReservedMemberNameOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal static readonly int op_Multiply = 2;

            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            internal static Money op_Division(Money left, Money right) => left;

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Combine(Money left, Money right) => /*!*/left + right;
        }
        """;

    private const string UnaryAndBinaryMinusSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator -(Money value) => new Money(-value.Amount);

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator -(Money left, Money right) => new Money(left.Amount - right.Amount);
        }
        """;

    private const string EnumOperandSource = """
        namespace Fixtures;

        internal enum Color
        {
            None = 0,
            Red = 1,
        }

        internal static class Colors
        {
            internal static Color Next(Color color) => /*!*/color + 1;
        }
        """;

    private const string DynamicOperandSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static dynamic Combine(dynamic left, dynamic right) => /*!*/left + right;
        }
        """;

    private const string ConstrainedGenericSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static TValue Combine<TValue>(TValue left, TValue right)
                where TValue : System.Numerics.INumber<TValue> => /*!*/left + right;
        }
        """;

    private const string PointerOperandSource = """
        namespace Fixtures;

        internal static class Pointers
        {
            internal static unsafe int* Advance(int* pointer, int offset) => /*!*/pointer + offset;
        }
        """;

    private const string ErrorTypeOperandSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static Missing Combine(Missing left, Missing right) => /*!*/left + right;
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Combines two numbers.
            internal static int Combine(int left, int right)
            {
                /* leading */
                return /*!*/left /* inner */ + /* after */ right; // tail
            }
        }
        """;

    private static readonly string[] _pointerErrorIds = ["CS0227"];

    private static readonly string[] _errorTypeErrorIds = ["CS0246"];

    private static readonly string[] _arithmeticNames = ["add", "subtract", "multiply", "divide", "modulo"];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ArithmeticOperatorMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("arithmetic");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.ArithmeticOperator);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.AddExpression,
                    SyntaxKind.SubtractExpression,
                    SyntaxKind.MultiplyExpression,
                    SyntaxKind.DivideExpression,
                    SyntaxKind.ModuloExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_ArithmeticExpression_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(BinaryFixture("+"));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("+", "add", "subtract,multiply,divide,modulo")]
    [Arguments("-", "subtract", "add,multiply,divide,modulo")]
    [Arguments("*", "multiply", "add,subtract,divide,modulo")]
    [Arguments("/", "divide", "add,subtract,multiply,modulo")]
    [Arguments("%", "modulo", "add,subtract,multiply,divide")]
    public async Task CreateMutations_ArithmeticExpression_ProducesEveryCounterpart(
        string symbol,
        string originalName,
        string targetNames
    )
    {
        ArgumentNullException.ThrowIfNull(targetNames);

        var targets = SplitValues(targetNames);
        var result = Mutate(BinaryFixture(symbol));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"arithmetic.{originalName}-to-{target}")));
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"{symbol} => {SymbolOf(target)}")));
        _ = await Assert
            .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.ArithmeticOperator });
    }

    [Test]
    [Arguments("+", SyntaxKind.AddExpression)]
    [Arguments("-", SyntaxKind.SubtractExpression)]
    [Arguments("*", SyntaxKind.MultiplyExpression)]
    [Arguments("/", SyntaxKind.DivideExpression)]
    [Arguments("%", SyntaxKind.ModuloExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string symbol, SyntaxKind kind)
    {
        var mutator = new ArithmeticOperatorMutator();
        var result = Mutate(BinaryFixture(symbol));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(4);
    }

    [Test]
    [Arguments("left < right")]
    [Arguments("left == right")]
    [Arguments("left & right")]
    [Arguments("left << right")]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string expression)
    {
        var result = Mutate(ExpressionFixture(expression));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNodeIsTheOriginal_KeepsLocation()
    {
        var result = Mutate(BinaryFixture("+"));
        var mutation = Single(result.Mutations, "arithmetic.add-to-subtract");

        _ = await Assert.That(mutation.Original).IsEqualTo(result.Node);
        _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("left - right");
        _ = await Assert.That(mutation.Location).IsEqualTo(result.Node.GetLocation());
    }

    [Test]
    public async Task ApplyTo_AddToMultiply_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "arithmetic.add-to-multiply");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("+ /* after */", "* /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Combines two numbers.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("left /* inner */ * /* after */ right; // tail");
    }

    [Test]
    public async Task CreateMutations_StringLiteralConcatenation_ReturnsEmpty()
    {
        var result = Mutate(StringLiteralSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_StringOperands_ReturnsEmpty()
    {
        var result = Mutate(StringOperandSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DelegateOperands_ReturnsEmpty()
    {
        var result = Mutate(DelegateOperandSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithoutCounterpart_ReturnsEmpty()
    {
        var result = Mutate(AddOnlyOperatorSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithCounterpart_ProducesOnlyThatCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        string[] expectedDisplayNames = ["+ => -"];
        var result = Mutate(AddAndSubtractOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(expectedDisplayNames);
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorOnAClassWithEveryCounterpart_ProducesAllFour()
    {
        string[] expectedIds =
        [
            "arithmetic.add-to-divide",
            "arithmetic.add-to-modulo",
            "arithmetic.add-to-multiply",
            "arithmetic.add-to-subtract",
        ];
        var result = Mutate(AllOperatorsSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorOnAGenericType_ProducesOnlyTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-multiply"];
        string[] expectedDisplayNames = ["+ => *"];
        var result = Mutate(GenericOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(expectedDisplayNames);
    }

    /// <summary>
    /// A lifted operator on a nullable value type is bound to the operator declared on the underlying
    /// type, so the counterpart lookup has to succeed on that underlying type.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedUserDefinedOperator_ProducesOnlyTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        var result = Mutate(NullableLiftedOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The right operand is a <c>Cents</c>, but the bound operator is declared on <c>Money</c> and only
    /// reached through an implicit conversion. The counterpart has to be looked up on the declaring type,
    /// not on the operand type.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperatorReachedThroughAnImplicitConversion_UsesTheDeclaringType()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        var result = Mutate(ImplicitConversionOperatorSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ImplicitConversionOperatorSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.ContainingType.Name).IsEqualTo("Money");
        _ = await Assert.That(semanticModel.GetTypeInfo(binary.Right).Type?.Name).IsEqualTo("Cents");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// Only one side of the expression is a <see cref="string" />, the other one is a number or an
    /// <see cref="object" />. Every overload of the string concatenation converts the other side to
    /// <see cref="string" /> or to <see cref="object" />, which the operand check rejects.
    /// </summary>
    /// <param name="expression">The expression the case exercises.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("text + number")]
    [Arguments("number + text")]
    [Arguments("text + value")]
    [Arguments("value + text")]
    public async Task CreateMutations_StringOnOneSideOnly_ReturnsEmpty(string expression)
    {
        var result = Mutate(ConcatFixture(expression));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// Both operands are a user defined struct that converts implicitly to <see cref="string" />, so the
    /// expression binds to the string concatenation. The converted operand type is the one after that
    /// conversion, which is what keeps the concatenation out of this operator family.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperandsConvertedToStringImplicitly_ReturnsEmpty()
    {
        var result = Mutate(ImplicitStringConversionSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ImplicitStringConversionSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.ContainingType.SpecialType).IsEqualTo(SpecialType.System_String);
        _ = await Assert
            .That(semanticModel.GetTypeInfo(binary.Left).ConvertedType?.SpecialType)
            .IsEqualTo(SpecialType.System_String);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("decimal", "decimal", "/*!*/left + right", "add")]
    [Arguments("char", "int", "/*!*/left + right", "add")]
    [Arguments("int?", "int?", "/*!*/left + right", "add")]
    [Arguments("double", "double", "/*!*/left * right", "multiply")]
    [Arguments("int", "int", "checked(/*!*/left % right)", "modulo")]
    [Arguments("int", "int", "unchecked(/*!*/left - right)", "subtract")]
    [Arguments("System.IntPtr", "System.IntPtr", "/*!*/left / right", "divide")]
    public async Task CreateMutations_ArithmeticOperandType_ProducesEveryCounterpart(
        string operandType,
        string resultType,
        string expression,
        string originalName
    )
    {
        var result = Mutate(OperandFixture(operandType, resultType, expression));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts(originalName));
    }

    /// <summary>
    /// An enum operand is arithmetic as far as this operator is concerned, even though only the additive
    /// mutants of <c>color + 1</c> bind. Whether a mutant compiles is decided when the mutant is built,
    /// not here, so all four counterparts are offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_EnumOperand_ProducesEveryCounterpart()
    {
        var result = Mutate(EnumOperandSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts("add"));
    }

    /// <summary>
    /// A dynamic expression binds to the built-in operator of <c>dynamic</c> rather than to a user defined
    /// one, so the user defined operator filter cannot narrow the result and every counterpart is offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_DynamicOperands_ProducesEveryCounterpart()
    {
        var result = Mutate(DynamicOperandSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(DynamicOperandSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MethodKind).IsEqualTo(MethodKind.BuiltinOperator);
        _ = await Assert.That(bound?.ToDisplayString()).IsEqualTo("dynamic.operator +(dynamic, dynamic)");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts("add"));
    }

    /// <summary>
    /// The result type of the user defined operator is unrelated to the result type of its counterpart,
    /// and neither is looked at: only the declared counterpart decides.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithAnotherResultType_ProducesThatCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        var result = Mutate(MismatchedResultTypeOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// In a checked context the expression binds to <c>op_CheckedAddition</c>. The counterpart is still
    /// looked up under the unchecked metadata name, which a type declaring a checked operator always has
    /// to provide as well.
    /// </summary>
    [Test]
    public async Task CreateMutations_CheckedUserDefinedOperator_ProducesTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        var result = Mutate(CheckedOperatorSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CheckedOperatorSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MetadataName).IsEqualTo("op_CheckedAddition");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// A member that carries the metadata name of an operator without being one is no counterpart: the
    /// field named <c>op_Multiply</c> and the ordinary method named <c>op_Division</c> are both skipped.
    /// </summary>
    [Test]
    public async Task CreateMutations_MembersNamedLikeOperators_AreNoCounterparts()
    {
        string[] expectedIds = ["arithmetic.add-to-subtract"];
        var result = Mutate(ReservedMemberNameOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The operator is declared on the constraint interface, and that interface declares nothing but its
    /// own operator, so no counterpart is found. The expression stays unmutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperatorOfAConstraintInterface_ReturnsEmpty()
    {
        var result = Mutate(ConstrainedGenericSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ConstrainedGenericSource);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(binary).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MethodKind).IsEqualTo(MethodKind.UserDefinedOperator);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// A pointer operand is not arithmetic here, because pointer arithmetic scales by the element size
    /// and none of the other four operators is even defined for it.
    /// </summary>
    [Test]
    public async Task CreateMutations_PointerOperands_ReturnsEmpty()
    {
        var result = MutateWithoutFixtureCheck(PointerOperandSource);

        _ = await Assert.That(result.ErrorIds).IsEquivalentTo(_pointerErrorIds);
        _ = await Assert.That(result.LeftType?.TypeKind).IsEqualTo(TypeKind.Pointer);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// An operand whose type could not be resolved is not mutated, so that a broken build does not turn
    /// into a flood of mutants.
    /// </summary>
    [Test]
    public async Task CreateMutations_ErrorTypeOperands_ReturnsEmpty()
    {
        var result = MutateWithoutFixtureCheck(ErrorTypeOperandSource);

        _ = await Assert.That(result.ErrorIds).IsEquivalentTo(_errorTypeErrorIds);
        _ = await Assert.That(result.LeftType?.TypeKind).IsEqualTo(TypeKind.Error);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(BinaryFixture("+"));
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new ArithmeticOperatorMutator();
        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToArray()
        );

        _ = await Assert.That(exception.CancellationToken).IsEqualTo(cancellation.Token);
    }

    /// <summary>
    /// The mapping tables of this operator only ever see the five kinds it supports, which the base class
    /// guarantees. Their default arm cannot be removed, because the compiler demands an exhaustive switch,
    /// so it is invoked directly to pin the exception it produces.
    /// </summary>
    /// <param name="mapperName">The name of the mapping method under test.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("GetName")]
    [Arguments("GetSymbol")]
    [Arguments("GetOperatorToken")]
    [Arguments("GetMetadataName")]
    public async Task Mapper_SyntaxKindIsNotArithmetic_ThrowsArgumentOutOfRangeException(string mapperName)
    {
        var exception = InvokeMapper(mapperName, SyntaxKind.BitwiseAndExpression);

        _ = await Assert.That(exception.ParamName).IsEqualTo("expressionKind");
        _ = await Assert.That(exception.ActualValue).IsEqualTo(SyntaxKind.BitwiseAndExpression);
        _ = await Assert.That(exception.Message).Contains("The syntax kind is not a binary arithmetic expression.");
    }

    /// <summary>
    /// A counterpart has to take as many operands as the operator it replaces. The unary minus of the
    /// fixture carries the metadata name <c>op_UnaryNegation</c>, so the binary <c>op_Subtraction</c> is no
    /// counterpart of it.
    /// </summary>
    [Test]
    public async Task HasCounterpart_OperandCountDiffers_ReturnsFalse()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(UnaryAndBinaryMinusSource);
        var money =
            compilation.GetTypeByMetadataName("Fixtures.Money")
            ?? throw new InvalidOperationException("The fixture does not declare 'Fixtures.Money'.");
        var unaryMinus = money.GetMembers("op_UnaryNegation").OfType<IMethodSymbol>().Single();
        var binaryMinus = money.GetMembers("op_Subtraction").OfType<IMethodSymbol>().Single();

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
        _ = await Assert.That(unaryMinus.Parameters.Length).IsEqualTo(1);
        _ = await Assert.That(InvokeHasCounterpart(unaryMinus, "op_Subtraction")).IsFalse();
        _ = await Assert.That(InvokeHasCounterpart(binaryMinus, "op_Addition")).IsTrue();
    }

    private static string BinaryFixture(string symbol) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static int Combine(int left, int right) => /*!*/left {{symbol}} right;
            }
            """;

    private static string ExpressionFixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static void Apply(int left, int right)
                {
                    _ = /*!*/{{expression}};
                }
            }
            """;

    /// <summary>
    /// Builds a fixture whose marked expression mixes a <see cref="string" /> with a value of another type.
    /// </summary>
    /// <param name="expression">The expression to mark, written over the parameters of the fixture.</param>
    /// <returns>The fixture source.</returns>
    private static string ConcatFixture(string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Text
            {
                internal static string Combine(int number, string text, object value) => /*!*/{{expression}};
            }
            """;

    /// <summary>
    /// Builds a fixture over two operands of the same type. The expression carries the marker itself, so
    /// that a case can wrap the marked expression into a <c>checked</c> or <c>unchecked</c> context.
    /// </summary>
    /// <param name="operandType">The declared type of both operands.</param>
    /// <param name="resultType">The declared result type of the fixture method.</param>
    /// <param name="expression">The expression, containing the marker in front of the binary expression.</param>
    /// <returns>The fixture source.</returns>
    private static string OperandFixture(string operandType, string resultType, string expression) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static {{resultType}} Combine({{operandType}} left, {{operandType}} right) =>
                    {{expression}};
            }
            """;

    /// <summary>
    /// The operator identifiers of all four counterparts of <paramref name="originalName" />.
    /// </summary>
    /// <param name="originalName">The name of the original operator, e.g. <c>add</c>.</param>
    /// <returns>The expected operator identifiers, sorted.</returns>
    private static ImmutableArray<string> AllCounterparts(string originalName) =>
        Sorted(
            _arithmeticNames
                .Where(name => !string.Equals(name, originalName, StringComparison.Ordinal))
                .Select(name => $"arithmetic.{originalName}-to-{name}")
        );

    /// <summary>
    /// Invokes one of the private mapping tables of the operator. Their default arm is unreachable through
    /// the public path, but the compiler requires it, so a test can only reach it directly.
    /// </summary>
    /// <param name="mapperName">The name of the mapping method.</param>
    /// <param name="expressionKind">The syntax kind to map.</param>
    /// <returns>The exception the mapping method produced.</returns>
    /// <exception cref="InvalidOperationException">
    /// The mapping method no longer exists or accepted the syntax kind.
    /// </exception>
    private static ArgumentOutOfRangeException InvokeMapper(string mapperName, SyntaxKind expressionKind)
    {
        var mapper =
            typeof(ArithmeticOperatorMutator).GetMethod(mapperName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"The mapping method '{mapperName}' no longer exists.");

        try
        {
            _ = mapper.Invoke(null, [expressionKind]);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is ArgumentOutOfRangeException expected)
        {
            return expected;
        }

        throw new InvalidOperationException(
            $"The mapping method '{mapperName}' accepted the syntax kind '{expressionKind}'."
        );
    }

    /// <summary>
    /// Invokes the private counterpart lookup of the operator, which is the only way to reach it with an
    /// operator the language cannot produce for one of the five supported syntax kinds.
    /// </summary>
    /// <param name="userDefinedOperator">The operator to find a counterpart for.</param>
    /// <param name="metadataName">The metadata name of the wanted counterpart.</param>
    /// <returns>Whether the declaring type provides such a counterpart.</returns>
    /// <exception cref="InvalidOperationException">The lookup no longer exists.</exception>
    private static bool InvokeHasCounterpart(IMethodSymbol userDefinedOperator, string metadataName)
    {
        var lookup =
            typeof(ArithmeticOperatorMutator).GetMethod("HasCounterpart", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The counterpart lookup no longer exists.");

        return (bool)lookup.Invoke(null, [userDefinedOperator, metadataName])!;
    }

    private static string SymbolOf(string name) =>
        name switch
        {
            "add" => "+",
            "subtract" => "-",
            "multiply" => "*",
            "divide" => "/",
            "modulo" => "%",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown arithmetic operator name."),
        };

    /// <summary>
    /// Splits a comma separated expectation of an inline data case. Reading the parameter here instead
    /// of in the public test method keeps the null contract of the test signature simple.
    /// </summary>
    /// <param name="values">The comma separated expectation.</param>
    /// <returns>The single expectations.</returns>
    private static string[] SplitValues(string values) => values.Split(',');

    private static ImmutableArray<string> Sorted(IEnumerable<string> values) =>
        [.. values.OrderBy(value => value, StringComparer.Ordinal)];

    private static Mutation Single(ImmutableArray<Mutation> mutations, string operatorId) =>
        mutations.Single(mutation => string.Equals(mutation.OperatorId, operatorId, StringComparison.Ordinal));

    private static (ImmutableArray<Mutation> Mutations, SyntaxTree Tree, ExpressionSyntax Node) Mutate(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var errors = CompilationFactory.GetCompileErrors(compilation);
        if (!errors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The fixture does not compile: {DiagnosticAssertions.Describe(errors)}"
            );
        }

        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new ArithmeticOperatorMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }

    /// <summary>
    /// Mutates a fixture that cannot compile, which is the only way to hand an operand of an error type or
    /// of a pointer type to the operator. The reported error identifiers are part of the result, so that a
    /// test can prove its fixture fails for exactly the intended reason.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>The produced mutations, the distinct error identifiers and the left operand type.</returns>
    private static (
        ImmutableArray<Mutation> Mutations,
        ImmutableArray<string> ErrorIds,
        ITypeSymbol? LeftType
    ) MutateWithoutFixtureCheck(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var binary = SyntaxNodeLocator.FindMarked<BinaryExpressionSyntax>(tree);
        var mutator = new ArithmeticOperatorMutator();
        var errorIds = Sorted(
            CompilationFactory
                .GetCompileErrors(compilation)
                .Select(diagnostic => diagnostic.Id)
                .Distinct(StringComparer.Ordinal)
        );

        return (
            [.. mutator.CreateMutations(binary, semanticModel, CancellationToken.None)],
            errorIds,
            semanticModel.GetTypeInfo(binary.Left).ConvertedType
        );
    }
}
