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

    private const string EnumMemberSource = """
        public enum Level
        {
            Low = 42,
        }
        """;

    private const string EnumTargetSource = """
        public enum Level
        {
            None,
            Low,
        }

        public class Sample
        {
            public Level Get() => 0;
        }
        """;

    /// <summary>
    /// A literal whose converted type is an error type. The literal has to be assigned to an unresolved
    /// type for that: with an unresolved return type it simply keeps its own type.
    /// </summary>
    private const string ErrorTargetSource = """
        public class Sample
        {
            public void Get()
            {
                Missing value = 42;
            }
        }
        """;

    /// <summary>
    /// The warning code of a <c>#pragma warning</c> directive is parsed as a numeric literal expression, but
    /// it lives inside directive trivia instead of inside a member.
    /// </summary>
    private const string PragmaWarningSource = """
        #pragma warning disable 1591

        public class Sample
        {
            public int Get() => 42;
        }
        """;

    private const string ByteArgumentSource = """
        public class Sample
        {
            public static void Use(byte value)
            {
            }

            public void Get() => Use(255);
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
    [Arguments("int", "0x10")]
    [Arguments("int", "0b1010")]
    [Arguments("int", "1_000")]
    [Arguments("long", "0xFFL")]
    [Arguments("uint", "4294967295u")]
    [Arguments("ulong", "18446744073709551615UL")]
    [Arguments("double", "1_000.5")]
    [Arguments("double", "0e0")]
    [Arguments("double", "1.5e3")]
    [Arguments("decimal", "0.0m")]
    [Arguments("int", "0B1010")]
    [Arguments("long", "0XFFL")]
    [Arguments("long", "1_000L")]
    [Arguments("uint", "0x10u")]
    [Arguments("uint", "0xFFu")]
    [Arguments("ulong", "0xFFul")]
    [Arguments("ulong", "0b1010UL")]
    public async Task Fixture_LiteralFormat_Compiles(string type, string literal)
    {
        var (compilation, _, _) = CompilationFactory.CreateWithModel(Fixture(type, literal));

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
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

    /// <summary>
    /// At the upper bound of the literal's converted type the <c>n + 1</c> candidate does not fit any
    /// more, so only the decrement survives.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The literal at the upper bound of <paramref name="type" />.</param>
    /// <param name="expected">The expected replacement text of the surviving decrement.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("byte", "255", "254")]
    [Arguments("sbyte", "127", "126")]
    [Arguments("short", "32767", "32766")]
    [Arguments("ushort", "65535", "65534")]
    [Arguments("int", "2147483647", "2147483646")]
    [Arguments("uint", "4294967295", "4294967294")]
    [Arguments("uint", "4294967295u", "4294967294u")]
    [Arguments("long", "9223372036854775807", "9223372036854775806")]
    [Arguments("long", "9223372036854775807L", "9223372036854775806L")]
    [Arguments("ulong", "18446744073709551615", "18446744073709551614")]
    [Arguments("ulong", "18446744073709551615UL", "18446744073709551614UL")]
    public async Task CreateMutations_MaximumOfItsType_OffersOnlyTheDecrement(
        string type,
        string literal,
        string expected
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => {expected}");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, expected));
    }

    /// <summary>
    /// The overflow guard also holds when the converted type carries no integral range at all, which is
    /// the case for a boxed literal: then the literal's own type decides.
    /// </summary>
    /// <param name="literal">The boxed literal at the upper bound of its own type.</param>
    /// <param name="expected">The expected replacement text of the surviving decrement.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("2147483647", "2147483646")]
    [Arguments("4294967295u", "4294967294u")]
    [Arguments("9223372036854775807L", "9223372036854775806L")]
    [Arguments("18446744073709551615UL", "18446744073709551614UL")]
    public async Task CreateMutations_BoxedMaximum_OffersOnlyTheDecrement(string literal, string expected)
    {
        var (tree, mutations) = Run(Fixture("object", literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => {expected}");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture("object", expected));
    }

    /// <summary>
    /// A zero literal never offers <c>n - 1</c>, which keeps the mutant inside the range of every
    /// unsigned type.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The zero literal, possibly suffixed.</param>
    /// <param name="expected">The expected replacement text.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("byte", "0", "1")]
    [Arguments("sbyte", "0", "1")]
    [Arguments("short", "0", "1")]
    [Arguments("ushort", "0", "1")]
    [Arguments("uint", "0", "1")]
    [Arguments("uint", "0u", "1u")]
    [Arguments("ulong", "0", "1")]
    [Arguments("ulong", "0UL", "1UL")]
    public async Task CreateMutations_ZeroAtTheLowerBound_OffersOnlyTheZeroToOneMutation(
        string type,
        string literal,
        string expected
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("0 => 1");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, expected));
    }

    /// <summary>
    /// The <c>1 =&gt; 0</c> special case keeps the suffix of the original literal.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The one literal, possibly suffixed.</param>
    /// <param name="expected">The expected replacement text.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("long", "1L", "0L")]
    [Arguments("uint", "1u", "0u")]
    [Arguments("ulong", "1UL", "0UL")]
    [Arguments("byte", "1", "0")]
    public async Task CreateMutations_OneAtTheLowerBound_OffersOnlyTheOneToZeroMutation(
        string type,
        string literal,
        string expected
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.one-to-zero");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("1 => 0");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, expected));
    }

    /// <summary>
    /// Away from the bounds both neighbours are offered. The replacement is always written in decimal
    /// notation and carries the suffix of the original literal, whatever notation that literal used.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The literal as written in the source.</param>
    /// <param name="incremented">The expected replacement text of the increment.</param>
    /// <param name="decremented">The expected replacement text of the decrement.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("byte", "2", "3", "1")]
    [Arguments("long", "5", "6", "4")]
    [Arguments("long", "5L", "6L", "4L")]
    [Arguments("uint", "5u", "6u", "4u")]
    [Arguments("ulong", "5UL", "6UL", "4UL")]
    [Arguments("int", "0x10", "17", "15")]
    [Arguments("int", "0b1010", "11", "9")]
    [Arguments("int", "1_000", "1001", "999")]
    [Arguments("long", "0xFFL", "256L", "254L")]
    [Arguments("int", "0B1010", "11", "9")]
    [Arguments("long", "0XFFL", "256L", "254L")]
    [Arguments("long", "1_000L", "1001L", "999L")]
    [Arguments("uint", "0x10u", "17u", "15u")]
    [Arguments("uint", "0xFFu", "256u", "254u")]
    [Arguments("ulong", "0xFFul", "256ul", "254ul")]
    [Arguments("ulong", "0b1010UL", "11UL", "9UL")]
    public async Task CreateMutations_LiteralWithinRange_OffersBothNeighbours(
        string type,
        string literal,
        string incremented,
        string decremented
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => {incremented}");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo($"{literal} => {decremented}");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, incremented));
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo(Fixture(type, decremented));
    }

    /// <summary>
    /// A negative number is a unary minus in front of a positive literal, so the operator mutates that
    /// positive literal and the range guard applies to it, not to the negated value.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_NegatedIntegerLiteral_MutatesThePositiveToken()
    {
        var (tree, mutations) = Run("public class Sample { public int Get() => -5; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("5 => 6");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("5 => 4");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo("public class Sample { public int Get() => -6; }");
        _ = await Assert.That(Rewrite(tree, mutations[1])).IsEqualTo("public class Sample { public int Get() => -4; }");
    }

    /// <summary>
    /// The literal of <c>-128</c> is the <see langword="int" /> literal <c>128</c>, therefore the
    /// <see langword="sbyte" /> range does not constrain it and both neighbours are offered.
    /// </summary>
    /// <param name="type">The declared type of the negated literal.</param>
    /// <param name="literal">The positive token of the negated minimum.</param>
    /// <param name="incremented">The expected replacement text of the increment.</param>
    /// <param name="decremented">The expected replacement text of the decrement.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("sbyte", "128", "129", "127")]
    [Arguments("short", "32768", "32769", "32767")]
    public async Task CreateMutations_NegatedMinimum_IsBoundByTheTokenTypeNotTheTargetType(
        string type,
        string literal,
        string incremented,
        string decremented
    )
    {
        var (_, mutations) = Run(Fixture(type, "-" + literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => {incremented}");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo($"{literal} => {decremented}");
    }

    [Test]
    public async Task CreateMutations_NullableTargetType_UsesTheUnderlyingRange()
    {
        var (_, mutations) = Run("public class Sample { public byte? Get() => 255; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("255 => 254");
    }

    /// <summary>
    /// A converted type that is no named type at all has no underlying type to unwrap and no integral range
    /// either, so the range of the literal's own type decides. <see langword="dynamic" /> is the simplest
    /// such type: it is an <c>IDynamicTypeSymbol</c> and never an <c>INamedTypeSymbol</c>.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_DynamicTargetType_OffersBothNeighbours()
    {
        var (tree, mutations) = Run("public class Sample { public dynamic Get() => 42; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("42 => 43");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("42 => 41");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public dynamic Get() => 43; }");
    }

    [Test]
    public async Task CreateMutations_ArgumentConvertedToByte_OffersOnlyTheDecrement()
    {
        var (_, mutations) = Run(ByteArgumentSource);

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("255 => 254");
    }

    /// <summary>
    /// The target type is wide enough for the incremented value, but the literal's own type is not, so the
    /// increment is dropped by the token type instead of by the target type.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The literal at the upper bound of its own type.</param>
    /// <param name="expected">The expected replacement text of the surviving decrement.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("long", "2147483647", "2147483646")]
    [Arguments("ulong", "4294967295u", "4294967294u")]
    public async Task CreateMutations_MaximumOfTheTokenTypeInAWiderTarget_OffersOnlyTheDecrement(
        string type,
        string literal,
        string expected
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => {expected}");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, expected));
    }

    /// <summary>
    /// A literal that does not fit its target type at all has no neighbour inside that range either, so
    /// neither candidate survives. The fixture deliberately does not compile.
    /// </summary>
    [Test]
    public async Task CreateMutations_ValueOutsideTheTargetRange_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("byte", "300"));
        var literal = (LiteralExpressionSyntax)FindNumericLiteral(tree);
        var mutator = new NumericLiteralMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(literal, semanticModel, CancellationToken.None)];

        _ = await Assert
            .That(semanticModel.GetTypeInfo(literal).ConvertedType?.SpecialType)
            .IsEqualTo(SpecialType.System_Byte);
        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// An array size written into the type of a field is parsed as a numeric literal expression, but it sits
    /// in a type context outside every member body, so the semantic model never binds it: the literal has no
    /// constant value at all and no mutation can be derived from it. The fixture deliberately does not
    /// compile, because C# has no legal position for an unbound numeric literal.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_LiteralWithoutAConstantValue_ReturnsEmpty()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(
            "public class Sample { private int[42] _values; }"
        );
        var literal = FindNumericLiteral(tree);
        var mutator = new NumericLiteralMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(literal, semanticModel, CancellationToken.None)];

        _ = await Assert.That(semanticModel.GetConstantValue(literal).HasValue).IsFalse();
        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// A field declaration without the <see langword="const" /> modifier is an ordinary initializer, so the
    /// walk up the parent chain has to continue past it instead of treating it as a constant context.
    /// </summary>
    [Test]
    public async Task CreateMutations_NonConstantFieldInitializer_IsMutated()
    {
        var (tree, mutations) = Run("public class Sample { private int _value = 42; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("42 => 43");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("42 => 41");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { private int _value = 43; }");
    }

    /// <summary>
    /// A local declaration without the <see langword="const" /> modifier is an ordinary initializer as well,
    /// which is the other half of the <see langword="const" /> guard on the walk up the parent chain.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_NonConstantLocalDeclaration_IsMutated()
    {
        var (tree, mutations) = Run("public class Sample { public int Get() { int value = 42; return value; } }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("42 => 43");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[1].DisplayName).IsEqualTo("42 => 41");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public int Get() { int value = 43; return value; } }");
    }

    /// <summary>
    /// The range guard looks at the type, never at the arithmetic context, so a
    /// <see langword="checked" /> and an <see langword="unchecked" /> expression behave the same.
    /// </summary>
    /// <param name="expression">The literal wrapped in an arithmetic context.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("checked(2147483647)")]
    [Arguments("unchecked(2147483647)")]
    public async Task CreateMutations_MaximumInArithmeticContext_OffersOnlyTheDecrement(string expression)
    {
        var (_, mutations) = Run(Fixture("int", expression));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("2147483647 => 2147483646");
    }

    /// <summary>
    /// An integral literal stays integral even when it is converted to a floating point type, so it is
    /// incremented and decremented instead of negated.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_IntegerLiteralInFloatingContext_StaysIntegral()
    {
        var (tree, mutations) = Run("public class Sample { public double Get() => 5; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(2);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.increment");
        _ = await Assert.That(mutations[1].OperatorId).IsEqualTo("numeric-literal.decrement");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public double Get() => 6; }");
    }

    [Test]
    public async Task CreateMutations_IntegerZeroInFloatingContext_ReturnsTheIntegralOne()
    {
        var (tree, mutations) = Run("public class Sample { public float Get() => 0; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public float Get() => 1; }");
    }

    /// <summary>
    /// A non zero floating point literal is negated, keeping its notation and its suffix.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The literal as written in the source.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("double", "2.5")]
    [Arguments("double", "1.5d")]
    [Arguments("double", "1.5e3")]
    [Arguments("double", "2.5e-3")]
    [Arguments("double", "1_000.5")]
    [Arguments("float", "1.5f")]
    [Arguments("float", "1.5F")]
    [Arguments("decimal", "1.5m")]
    [Arguments("decimal", "1.5M")]
    public async Task CreateMutations_FloatingLiteral_NegatesItKeepingTheNotation(string type, string literal)
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.negate");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo($"{literal} => -{literal}");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, "-" + literal));
    }

    /// <summary>
    /// A floating point zero is replaced by one, written in the notation the target type requires.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The zero literal as written in the source.</param>
    /// <param name="expected">The expected replacement text.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("double", "0.0", "1.0")]
    [Arguments("double", "0e0", "1.0")]
    [Arguments("double", "0d", "1d")]
    [Arguments("double", "0D", "1D")]
    [Arguments("double", "0.0d", "1d")]
    [Arguments("float", "0f", "1f")]
    [Arguments("float", "0F", "1F")]
    [Arguments("float", "0.0f", "1f")]
    [Arguments("decimal", "0m", "1m")]
    [Arguments("decimal", "0M", "1M")]
    [Arguments("decimal", "0.0m", "1m")]
    public async Task CreateMutations_FloatingZero_ReturnsTheTypedOneLiteral(
        string type,
        string literal,
        string expected
    )
    {
        var (tree, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert.That(mutations[0].DisplayName).IsEqualTo("0 => 1");
        _ = await Assert.That(Rewrite(tree, mutations[0])).IsEqualTo(Fixture(type, expected));
    }

    /// <summary>
    /// The zero check comes before the check for an already negated literal, so a negative zero is
    /// turned into one rather than being left alone.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_NegativeFloatingZero_ReturnsTheZeroToOneMutation()
    {
        var (tree, mutations) = Run("public class Sample { public double Get() => -0.0; }");

        _ = await Assert.That(mutations).Count().IsEqualTo(1);
        _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("numeric-literal.zero-to-one");
        _ = await Assert
            .That(Rewrite(tree, mutations[0]))
            .IsEqualTo("public class Sample { public double Get() => -1.0; }");
    }

    /// <summary>
    /// A literal that is already negated is not negated a second time, because that mutation would
    /// produce the value the source already computes.
    /// </summary>
    /// <param name="type">The declared type the literal is converted to.</param>
    /// <param name="literal">The negated literal as written in the source.</param>
    /// <returns>A task representing the test.</returns>
    [Test]
    [Arguments("double", "-2.5")]
    [Arguments("float", "-1.5f")]
    [Arguments("decimal", "-1.5m")]
    public async Task CreateMutations_AlreadyNegatedFloatingLiteral_ReturnsEmpty(string type, string literal)
    {
        var (_, mutations) = Run(Fixture(type, literal));

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EnumTargetType_ReturnsEmpty()
    {
        var (_, mutations) = Run(EnumTargetSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_ErrorTargetType_ReturnsEmpty()
    {
        var (_, mutations) = Run(ErrorTargetSource);

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
    public async Task CreateMutations_ConstantPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public bool Get(int value) => value is 42; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_RelationalPattern_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public bool Get(int value) => value is > 42; }");

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_EnumMemberValue_ReturnsEmpty()
    {
        var (_, mutations) = Run(EnumMemberSource);

        _ = await Assert.That(mutations).IsEmpty();
    }

    /// <summary>
    /// The warning code of a <c>#pragma warning</c> directive is a numeric literal expression whose parent
    /// chain ends at the directive: it passes neither a member declaration nor the compilation unit, so the
    /// walk looking for a position that requires a constant runs out of parents instead of finding a
    /// decision. The semantic model binds nothing there either, so the literal carries no constant value and
    /// no mutation can be derived from it.
    /// </summary>
    /// <returns>A task representing the test.</returns>
    [Test]
    public async Task CreateMutations_LiteralInsideADirective_ReturnsEmpty()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(PragmaWarningSource);
        var literal = FindDirectiveLiteral(tree);
        var mutator = new NumericLiteralMutator();
        Mutation[] mutations = [.. mutator.CreateMutations(literal, semanticModel, CancellationToken.None)];

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation)).IsEmpty();
        _ = await Assert.That(literal.ToString()).IsEqualTo("1591");
        _ = await Assert.That(literal.Parent?.Kind()).IsEqualTo(SyntaxKind.PragmaWarningDirectiveTrivia);
        _ = await Assert.That(literal.Parent?.Parent).IsNull();
        _ = await Assert.That(semanticModel.GetConstantValue(literal).HasValue).IsFalse();
        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var (_, mutations) = Run("public class Sample { public string Get() => \"a\"; }", FindStringLiteral);

        _ = await Assert.That(mutations).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NodeNull_ThrowsArgumentNullException()
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(Fixture("int", "42"));
        var mutator = new NumericLiteralMutator();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    public async Task CreateMutations_SemanticModelNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(Fixture("int", "42"));
        var mutator = new NumericLiteralMutator();
        var node = FindNumericLiteral(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    public async Task CreateMutations_CancelledToken_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Fixture("int", "42"));
        var mutator = new NumericLiteralMutator();
        var node = FindNumericLiteral(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = mutator.CreateMutations(node, semanticModel, cancellation.Token).ToList()
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string Fixture(string type, string literal) =>
        $"public class Sample {{ public {type} Get() => {literal}; }}";

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

    /// <summary>
    /// Finds the numeric literal of a directive, which the ordinary lookup never sees: the default descent
    /// stops at trivia, and a directive lives inside trivia.
    /// </summary>
    /// <param name="tree">The tree to search.</param>
    /// <returns>The first numeric literal of the tree, trivia included.</returns>
    private static LiteralExpressionSyntax FindDirectiveLiteral(SyntaxTree tree) =>
        tree.GetRoot()
            .DescendantNodes(descendIntoTrivia: true)
            .OfType<LiteralExpressionSyntax>()
            .First(static literal => literal.IsKind(SyntaxKind.NumericLiteralExpression));

    private static SyntaxNode FindStringLiteral(SyntaxTree tree) =>
        SyntaxNodeLocator.FindFirst<LiteralExpressionSyntax>(
            tree,
            static literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
        );
}
