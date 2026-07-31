namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Reflection;
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
/// Covers the compound arithmetic assignment mutations together with the guards that keep string
/// appends, delegate combinations and event subscriptions out of this operator family.
/// </summary>
public class ArithmeticAssignmentMutatorTests
{
    private const string StringAppendLiteralSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Append(string text)
            {
                /*!*/text += "x";
                return text;
            }
        }
        """;

    private const string StringAppendVariableSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Append(string text, string suffix)
            {
                /*!*/text += suffix;
                return text;
            }
        }
        """;

    private const string EventSubscriptionSource = """
        namespace Fixtures;

        internal sealed class Publisher
        {
            internal event System.EventHandler? Changed;

            internal void Subscribe(System.EventHandler handler) => /*!*/Changed += handler;
        }
        """;

    private const string DelegateAppendSource = """
        namespace Fixtures;

        internal static class Handlers
        {
            internal static System.Action Combine(System.Action left, System.Action right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string AddAndMultiplyOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static Money operator *(Money left, Money right) => new Money(left.Amount * right.Amount);
        }

        internal static class Wallet
        {
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Vector Accumulate(Vector left, Vector right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string GenericOperatorSource = """
        namespace Fixtures;

        internal readonly struct Box<TValue>
        {
            internal Box(TValue value) => Value = value;

            internal TValue Value { get; }

            public static Box<TValue> operator +(Box<TValue> left, Box<TValue> right) => left;

            public static Box<TValue> operator %(Box<TValue> left, Box<TValue> right) => right;
        }

        internal static class Boxes
        {
            internal static Box<int> Accumulate(Box<int> left, Box<int> right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Money? Accumulate(Money? left, Money? right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Money Accumulate(Money left, Cents right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string StringAppendOfNumberSource = """
        namespace Fixtures;

        internal static class Text
        {
            internal static string Append(string text, int number)
            {
                /*!*/text += number;
                return text;
            }
        }
        """;

    private const string StringAppendOfSlugSource = """
        namespace Fixtures;

        internal readonly struct Slug
        {
            internal Slug(string value) => Value = value;

            internal string Value { get; }

            public static implicit operator string(Slug value) => value.Value;
        }

        internal static class Text
        {
            internal static string Append(string text, Slug slug)
            {
                /*!*/text += slug;
                return text;
            }
        }
        """;

    private const string MismatchedResultTypeOperatorSource = """
        namespace Fixtures;

        internal readonly struct Money
        {
            internal Money(int amount) => Amount = amount;

            internal int Amount { get; }

            public static Money operator +(Money left, Money right) => new Money(left.Amount + right.Amount);

            public static string operator -(Money left, Money right) => "money";
        }

        internal static class Wallet
        {
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                checked
                {
                    /*!*/total += right;
                }

                return total;
            }
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
            internal static Money Accumulate(Money left, Money right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
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

    private const string EnumTargetSource = """
        namespace Fixtures;

        internal enum Color
        {
            None = 0,
            Red = 1,
        }

        internal static class Colors
        {
            internal static Color Next(Color color)
            {
                /*!*/color += 1;
                return color;
            }
        }
        """;

    private const string DynamicTargetSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static dynamic Accumulate(dynamic total, dynamic value)
            {
                /*!*/total += value;
                return total;
            }
        }
        """;

    private const string ConstrainedGenericSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static TValue Accumulate<TValue>(TValue left, TValue right)
                where TValue : System.Numerics.INumber<TValue>
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string PointerTargetSource = """
        namespace Fixtures;

        internal static class Pointers
        {
            internal static unsafe int* Advance(int* pointer, int offset)
            {
                /*!*/pointer += offset;
                return pointer;
            }
        }
        """;

    private const string ErrorTypeTargetSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            internal static Missing Accumulate(Missing left, Missing right)
            {
                var total = left;
                /*!*/total += right;
                return total;
            }
        }
        """;

    private const string TriviaSource = """
        namespace Fixtures;

        internal static class Calculator
        {
            // Accumulates a value.
            internal static int Accumulate(int total, int value)
            {
                /* leading */
                /*!*/total /* inner */ += /* after */ value; // tail
                return total;
            }
        }
        """;

    private static readonly string[] _pointerErrorIds = ["CS0227"];

    private static readonly string[] _errorTypeErrorIds = ["CS0246"];

    private static readonly string[] _arithmeticNames =
    [
        "add-assign",
        "subtract-assign",
        "multiply-assign",
        "divide-assign",
        "modulo-assign",
    ];

    [Test]
    public async Task Metadata_Operator_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ArithmeticAssignmentMutator();

        _ = await Assert.That(mutator.Id).IsEqualTo("arithmetic-assignment");
        _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.ArithmeticAssignment);
        _ = await Assert
            .That(mutator.SupportedSyntaxKinds)
            .IsEquivalentTo(
                new[]
                {
                    SyntaxKind.AddAssignmentExpression,
                    SyntaxKind.SubtractAssignmentExpression,
                    SyntaxKind.MultiplyAssignmentExpression,
                    SyntaxKind.DivideAssignmentExpression,
                    SyntaxKind.ModuloAssignmentExpression,
                }
            );
    }

    [Test]
    public async Task Fixture_CompoundAssignment_Compiles()
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(AssignmentFixture("+="));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
    }

    [Test]
    [Arguments("+=", "add-assign", "subtract-assign,multiply-assign,divide-assign,modulo-assign")]
    [Arguments("-=", "subtract-assign", "add-assign,multiply-assign,divide-assign,modulo-assign")]
    [Arguments("*=", "multiply-assign", "add-assign,subtract-assign,divide-assign,modulo-assign")]
    [Arguments("/=", "divide-assign", "add-assign,subtract-assign,multiply-assign,modulo-assign")]
    [Arguments("%=", "modulo-assign", "add-assign,subtract-assign,multiply-assign,divide-assign")]
    public async Task CreateMutations_CompoundAssignment_ProducesEveryCounterpart(
        string symbol,
        string originalName,
        string targetNames
    )
    {
        ArgumentNullException.ThrowIfNull(targetNames);

        var targets = SplitValues(targetNames);
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"arithmetic-assignment.{originalName}-to-{target}")));
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(Sorted(targets.Select(target => $"{symbol} => {SymbolOf(target)}")));
        _ = await Assert
            .That(result.Mutations.Select(mutation => mutation.Kind).Distinct())
            .IsEquivalentTo(new[] { MutationKind.ArithmeticAssignment });
    }

    [Test]
    [Arguments("+=", SyntaxKind.AddAssignmentExpression)]
    [Arguments("-=", SyntaxKind.SubtractAssignmentExpression)]
    [Arguments("*=", SyntaxKind.MultiplyAssignmentExpression)]
    [Arguments("/=", SyntaxKind.DivideAssignmentExpression)]
    [Arguments("%=", SyntaxKind.ModuloAssignmentExpression)]
    public async Task SupportedSyntaxKinds_EveryKind_IsHandledByCreateMutations(string symbol, SyntaxKind kind)
    {
        var mutator = new ArithmeticAssignmentMutator();
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(kind);
        _ = await Assert.That(mutator.SupportedSyntaxKinds).Contains(kind);
        _ = await Assert.That(result.Mutations).Count().IsEqualTo(4);
    }

    [Test]
    [Arguments("=")]
    [Arguments("<<=")]
    [Arguments(">>=")]
    [Arguments("&=")]
    [Arguments("|=")]
    [Arguments("^=")]
    public async Task CreateMutations_UnsupportedAssignmentKind_ReturnsEmpty(string symbol)
    {
        var result = Mutate(AssignmentFixture(symbol));

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_MutatedNodeIsTheOriginal_ReplacesOnlyTheOperator()
    {
        var result = Mutate(AssignmentFixture("+="));
        var mutation = Single(result.Mutations, "arithmetic-assignment.add-assign-to-modulo-assign");

        _ = await Assert.That(mutation.Original).IsEqualTo(result.Node);
        _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo("total %= value");
    }

    [Test]
    public async Task ApplyTo_AddAssignToDivideAssign_RewritesOperatorAndKeepsTrivia()
    {
        var result = Mutate(TriviaSource);
        var mutation = Single(result.Mutations, "arithmetic-assignment.add-assign-to-divide-assign");

        var mutated = mutation.ApplyTo(result.Tree).ToString();

        _ = await Assert
            .That(mutated)
            .IsEqualTo(TriviaSource.Replace("+= /* after */", "/= /* after */", StringComparison.Ordinal));
        _ = await Assert.That(mutated).Contains("// Accumulates a value.");
        _ = await Assert.That(mutated).Contains("/* leading */");
        _ = await Assert.That(mutated).Contains("total /* inner */ /= /* after */ value; // tail");
    }

    [Test]
    public async Task CreateMutations_StringAppendOfLiteral_ReturnsEmpty()
    {
        var result = Mutate(StringAppendLiteralSource);

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddAssignmentExpression);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_StringAppendOfVariable_ReturnsEmpty()
    {
        var result = Mutate(StringAppendVariableSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// The language requires the type of an event to be a delegate type, which is what keeps an event
    /// subscription out of this operator family. The test asserts that type, because the guard depends on it.
    /// The subscription binds to the add accessor of the event, which is an ordinary method symbol and not
    /// a user defined operator, so nothing but the delegate type of the left side rejects it.
    /// </summary>
    [Test]
    public async Task CreateMutations_EventSubscription_ReturnsEmpty()
    {
        var result = Mutate(EventSubscriptionSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(EventSubscriptionSource);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(assignment).Symbol;

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddAssignmentExpression);
        _ = await Assert.That(bound).IsNotNull();
        _ = await Assert.That(bound is IEventSymbol).IsFalse();
        _ = await Assert.That((bound as IMethodSymbol)?.MethodKind).IsEqualTo(MethodKind.EventAdd);
        _ = await Assert
            .That(semanticModel.GetTypeInfo(assignment.Left).ConvertedType?.TypeKind)
            .IsEqualTo(TypeKind.Delegate);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_DelegateAppend_ReturnsEmpty()
    {
        var result = Mutate(DelegateAppendSource);

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
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-multiply-assign"];
        string[] expectedDisplayNames = ["+= => *="];
        var result = Mutate(AddAndMultiplyOperatorSource);

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
            "arithmetic-assignment.add-assign-to-divide-assign",
            "arithmetic-assignment.add-assign-to-modulo-assign",
            "arithmetic-assignment.add-assign-to-multiply-assign",
            "arithmetic-assignment.add-assign-to-subtract-assign",
        ];
        var result = Mutate(AllOperatorsSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    [Test]
    public async Task CreateMutations_UserDefinedOperatorOnAGenericType_ProducesOnlyTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-modulo-assign"];
        string[] expectedDisplayNames = ["+= => %="];
        var result = Mutate(GenericOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.DisplayName)))
            .IsEquivalentTo(expectedDisplayNames);
    }

    /// <summary>
    /// A compound assignment over a nullable value type is bound to the lifted form of the operator
    /// declared on the underlying type, so the counterpart lookup has to succeed on that underlying type.
    /// </summary>
    [Test]
    public async Task CreateMutations_LiftedUserDefinedOperator_ProducesOnlyTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-subtract-assign"];
        var result = Mutate(NullableLiftedOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The right hand side is a <c>Cents</c>, but the bound operator is declared on <c>Money</c> and only
    /// reached through an implicit conversion. The counterpart has to be looked up on the declaring type.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperatorReachedThroughAnImplicitConversion_UsesTheDeclaringType()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-subtract-assign"];
        var result = Mutate(ImplicitConversionOperatorSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ImplicitConversionOperatorSource);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(assignment).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.ContainingType.Name).IsEqualTo("Money");
        _ = await Assert.That(semanticModel.GetTypeInfo(assignment.Right).Type?.Name).IsEqualTo("Cents");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The kind of the assignment target does not change the mutation set: a property, an indexer, a field
    /// and a <see langword="ref" /> local are all mutated the same way.
    /// </summary>
    /// <param name="target">The assignment target the case exercises.</param>
    /// <returns>A task that represents the asynchronous test.</returns>
    [Test]
    [Arguments("Total")]
    [Arguments("this[0]")]
    [Arguments("_field")]
    [Arguments("slot")]
    public async Task CreateMutations_AssignmentTarget_ProducesEveryCounterpart(string target)
    {
        var result = Mutate(TargetFixture(target));

        _ = await Assert.That(result.Node.Kind()).IsEqualTo(SyntaxKind.AddAssignmentExpression);
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts("add-assign"));
    }

    /// <summary>
    /// Only the target of the append is a <see cref="string" />, the appended value is a number. The append
    /// still belongs to the string operators, which the operand check ensures.
    /// </summary>
    [Test]
    public async Task CreateMutations_StringAppendOfANumber_ReturnsEmpty()
    {
        var result = Mutate(StringAppendOfNumberSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    /// <summary>
    /// The appended value is a struct that converts implicitly to <see cref="string" />, so the assignment
    /// binds to the string concatenation and stays out of this operator family.
    /// </summary>
    [Test]
    public async Task CreateMutations_StringAppendOfAnImplicitlyConvertedValue_ReturnsEmpty()
    {
        var result = Mutate(StringAppendOfSlugSource);

        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    [Arguments("decimal", "/*!*/total += value;", "add-assign")]
    [Arguments("char", "/*!*/total += value;", "add-assign")]
    [Arguments("int?", "/*!*/total += value;", "add-assign")]
    [Arguments("System.IntPtr", "/*!*/total /= value;", "divide-assign")]
    [Arguments("double", "checked { /*!*/total *= value; }", "multiply-assign")]
    [Arguments("int", "unchecked { /*!*/total %= value; }", "modulo-assign")]
    public async Task CreateMutations_ArithmeticOperandType_ProducesEveryCounterpart(
        string operandType,
        string statement,
        string originalName
    )
    {
        var result = Mutate(OperandFixture(operandType, operandType, statement));

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts(originalName));
    }

    /// <summary>
    /// An enum target is arithmetic as far as this operator is concerned, even though only the additive
    /// mutants of <c>color += 1</c> bind. Whether a mutant compiles is decided when the mutant is built.
    /// </summary>
    [Test]
    public async Task CreateMutations_EnumTarget_ProducesEveryCounterpart()
    {
        var result = Mutate(EnumTargetSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts("add-assign"));
    }

    /// <summary>
    /// A dynamic assignment binds to the built-in operator of <c>dynamic</c> rather than to a user defined
    /// one, so the user defined operator filter cannot narrow the result and every counterpart is offered.
    /// </summary>
    [Test]
    public async Task CreateMutations_DynamicTarget_ProducesEveryCounterpart()
    {
        var result = Mutate(DynamicTargetSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(DynamicTargetSource);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(assignment).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MethodKind).IsEqualTo(MethodKind.BuiltinOperator);
        _ = await Assert.That(bound?.ToDisplayString()).IsEqualTo("dynamic.operator +(dynamic, dynamic)");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(AllCounterparts("add-assign"));
    }

    /// <summary>
    /// The result type of the counterpart is not looked at, only its declaration.
    /// </summary>
    [Test]
    public async Task CreateMutations_UserDefinedOperatorWithAnotherResultType_ProducesThatCounterpart()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-subtract-assign"];
        var result = Mutate(MismatchedResultTypeOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// In a checked context the assignment binds to <c>op_CheckedAddition</c>. The counterpart is still
    /// looked up under the unchecked metadata name, which a type declaring a checked operator always has to
    /// provide as well.
    /// </summary>
    [Test]
    public async Task CreateMutations_CheckedUserDefinedOperator_ProducesTheDeclaredCounterpart()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-subtract-assign"];
        var result = Mutate(CheckedOperatorSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CheckedOperatorSource);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(assignment).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MetadataName).IsEqualTo("op_CheckedAddition");
        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// A member that carries the metadata name of an operator without being one is no counterpart: the field
    /// named <c>op_Multiply</c> and the ordinary method named <c>op_Division</c> are both skipped.
    /// </summary>
    [Test]
    public async Task CreateMutations_MembersNamedLikeOperators_AreNoCounterparts()
    {
        string[] expectedIds = ["arithmetic-assignment.add-assign-to-subtract-assign"];
        var result = Mutate(ReservedMemberNameOperatorSource);

        _ = await Assert
            .That(Sorted(result.Mutations.Select(mutation => mutation.OperatorId)))
            .IsEquivalentTo(expectedIds);
    }

    /// <summary>
    /// The operator is declared on the constraint interface, and that interface declares nothing but its own
    /// operator, so no counterpart is found and the assignment stays unmutated.
    /// </summary>
    [Test]
    public async Task CreateMutations_OperatorOfAConstraintInterface_ReturnsEmpty()
    {
        var result = Mutate(ConstrainedGenericSource);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(ConstrainedGenericSource);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var bound = semanticModel.GetSymbolInfo(assignment).Symbol as IMethodSymbol;

        _ = await Assert.That(bound?.MethodKind).IsEqualTo(MethodKind.UserDefinedOperator);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_PointerTarget_ReturnsEmpty()
    {
        var result = MutateWithoutFixtureCheck(PointerTargetSource);

        _ = await Assert.That(result.ErrorIds).IsEquivalentTo(_pointerErrorIds);
        _ = await Assert.That(result.LeftType?.TypeKind).IsEqualTo(TypeKind.Pointer);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ErrorTypeTarget_ReturnsEmpty()
    {
        var result = MutateWithoutFixtureCheck(ErrorTypeTargetSource);

        _ = await Assert.That(result.ErrorIds).IsEquivalentTo(_errorTypeErrorIds);
        _ = await Assert.That(result.LeftType?.TypeKind).IsEqualTo(TypeKind.Error);
        _ = await Assert.That(result.Mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(AssignmentFixture("+="));
        var node = SyntaxNodeLocator.FindMarked<ExpressionSyntax>(tree);
        var mutator = new ArithmeticAssignmentMutator();
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
    public async Task Mapper_SyntaxKindIsNoCompoundAssignment_ThrowsArgumentOutOfRangeException(string mapperName)
    {
        var exception = InvokeMapper(mapperName, SyntaxKind.AndAssignmentExpression);

        _ = await Assert.That(exception.ParamName).IsEqualTo("expressionKind");
        _ = await Assert.That(exception.ActualValue).IsEqualTo(SyntaxKind.AndAssignmentExpression);
        _ = await Assert.That(exception.Message).Contains("The syntax kind is not a compound arithmetic assignment.");
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

    private static string AssignmentFixture(string symbol) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static int Accumulate(int total, int value)
                {
                    /*!*/total {{symbol}} value;
                    return total;
                }
            }
            """;

    /// <summary>
    /// Builds a fixture whose marked assignment writes to <paramref name="target" />, so that every kind of
    /// assignment target can be exercised with the very same statement.
    /// </summary>
    /// <param name="target">The assignment target, e.g. <c>Total</c> or <c>this[0]</c>.</param>
    /// <returns>The fixture source.</returns>
    private static string TargetFixture(string target) =>
        $$"""
            namespace Fixtures;

            internal sealed class Counter
            {
                private readonly int[] _values = new int[4];

                private int _field;

                internal int Total { get; set; }

                internal int this[int index]
                {
                    get => _values[index];
                    set => _values[index] = value;
                }

                internal void Apply(int value)
                {
                    ref var slot = ref _field;
                    /*!*/{{target}} += value;
                }
            }
            """;

    /// <summary>
    /// Builds a fixture over a target and a value of the given types. The statement carries the marker
    /// itself, so that a case can wrap it into a <c>checked</c> or <c>unchecked</c> block.
    /// </summary>
    /// <param name="targetType">The declared type of the assignment target.</param>
    /// <param name="valueType">The declared type of the assigned value.</param>
    /// <param name="statement">The statement, containing the marker in front of the assignment.</param>
    /// <returns>The fixture source.</returns>
    private static string OperandFixture(string targetType, string valueType, string statement) =>
        $$"""
            namespace Fixtures;

            internal static class Calculator
            {
                internal static {{targetType}} Accumulate({{targetType}} total, {{valueType}} value)
                {
                    {{statement}}
                    return total;
                }
            }
            """;

    /// <summary>
    /// The operator identifiers of all four counterparts of <paramref name="originalName" />.
    /// </summary>
    /// <param name="originalName">The name of the original operator, e.g. <c>add-assign</c>.</param>
    /// <returns>The expected operator identifiers, sorted.</returns>
    private static ImmutableArray<string> AllCounterparts(string originalName) =>
        Sorted(
            _arithmeticNames
                .Where(name => !string.Equals(name, originalName, StringComparison.Ordinal))
                .Select(name => $"arithmetic-assignment.{originalName}-to-{name}")
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
            typeof(ArithmeticAssignmentMutator).GetMethod(mapperName, BindingFlags.NonPublic | BindingFlags.Static)
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
            typeof(ArithmeticAssignmentMutator).GetMethod(
                "HasCounterpart",
                BindingFlags.NonPublic | BindingFlags.Static
            ) ?? throw new InvalidOperationException("The counterpart lookup no longer exists.");

        return (bool)lookup.Invoke(null, [userDefinedOperator, metadataName])!;
    }

    private static string SymbolOf(string name) =>
        name switch
        {
            "add-assign" => "+=",
            "subtract-assign" => "-=",
            "multiply-assign" => "*=",
            "divide-assign" => "/=",
            "modulo-assign" => "%=",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown assignment operator name."),
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
        var mutator = new ArithmeticAssignmentMutator();

        return ([.. mutator.CreateMutations(node, semanticModel, CancellationToken.None)], tree, node);
    }

    /// <summary>
    /// Mutates a fixture that cannot compile, which is the only way to hand a target of an error type or of a
    /// pointer type to the operator. The reported error identifiers are part of the result, so that a test
    /// can prove its fixture fails for exactly the intended reason.
    /// </summary>
    /// <param name="source">The fixture source.</param>
    /// <returns>The produced mutations, the distinct error identifiers and the type of the target.</returns>
    private static (
        ImmutableArray<Mutation> Mutations,
        ImmutableArray<string> ErrorIds,
        ITypeSymbol? LeftType
    ) MutateWithoutFixtureCheck(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var assignment = SyntaxNodeLocator.FindMarked<AssignmentExpressionSyntax>(tree);
        var mutator = new ArithmeticAssignmentMutator();
        var errorIds = Sorted(
            CompilationFactory
                .GetCompileErrors(compilation)
                .Select(diagnostic => diagnostic.Id)
                .Distinct(StringComparer.Ordinal)
        );

        return (
            [.. mutator.CreateMutations(assignment, semanticModel, CancellationToken.None)],
            errorIds,
            semanticModel.GetTypeInfo(assignment.Left).ConvertedType
        );
    }
}
