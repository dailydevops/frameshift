namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the type-eligibility rules of the shared <see cref="IntegralTypeCheck.IsIntegral" /> helper,
/// which both <see cref="BitwiseOperatorMutator" /> and <see cref="BitwiseAssignmentMutator" /> rely on
/// to decide which operands belong to the bitwise mutation family.
/// </summary>
public class IntegralTypeCheckTests
{
    private const string Source = """
        namespace Fixtures;

        internal enum Flags
        {
            None = 0,
            First = 1,
        }

        internal static class Values
        {
            internal static void Use(
                sbyte sbyteValue,
                byte byteValue,
                short shortValue,
                ushort ushortValue,
                int intValue,
                uint uintValue,
                long longValue,
                ulong ulongValue,
                char charValue,
                bool boolValue,
                double doubleValue,
                string stringValue,
                int? nullableIntValue,
                Flags flagsValue,
                Flags? nullableFlagsValue
            ) { }
        }
        """;

    [Test]
    [Arguments("sbyteValue")]
    [Arguments("byteValue")]
    [Arguments("shortValue")]
    [Arguments("ushortValue")]
    [Arguments("intValue")]
    [Arguments("uintValue")]
    [Arguments("longValue")]
    [Arguments("ulongValue")]
    [Arguments("charValue")]
    public async Task IsIntegral_IntegralParameter_AllowEnumEitherWay_ReturnsTrue(string parameterName)
    {
        var type = GetParameterType(parameterName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: true)).IsTrue();
            _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: false)).IsTrue();
        }
    }

    [Test]
    [Arguments("boolValue")]
    [Arguments("doubleValue")]
    [Arguments("stringValue")]
    public async Task IsIntegral_NonIntegralParameter_ReturnsFalse(string parameterName)
    {
        var type = GetParameterType(parameterName);

        using (Assert.Multiple())
        {
            _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: true)).IsFalse();
            _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: false)).IsFalse();
        }
    }

    [Test]
    public async Task IsIntegral_NullableIntegralParameter_UnwrapsToTrue()
    {
        var type = GetParameterType("nullableIntValue");

        _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: false)).IsTrue();
    }

    [Test]
    public async Task IsIntegral_EnumParameter_AllowEnumTrue_ResolvesToUnderlyingType()
    {
        var type = GetParameterType("flagsValue");

        _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: true)).IsTrue();
    }

    [Test]
    public async Task IsIntegral_EnumParameter_AllowEnumFalse_ReturnsFalse()
    {
        var type = GetParameterType("flagsValue");

        _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: false)).IsFalse();
    }

    [Test]
    public async Task IsIntegral_NullableEnumParameter_AllowEnumTrue_ReturnsTrue()
    {
        var type = GetParameterType("nullableFlagsValue");

        _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: true)).IsTrue();
    }

    [Test]
    public async Task IsIntegral_NullableEnumParameter_AllowEnumFalse_ReturnsFalse()
    {
        var type = GetParameterType("nullableFlagsValue");

        _ = await Assert.That(IntegralTypeCheck.IsIntegral(type, allowEnum: false)).IsFalse();
    }

    [Test]
    public async Task IsIntegral_NullType_ReturnsFalse() =>
        await Assert.That(IntegralTypeCheck.IsIntegral(null, allowEnum: true)).IsFalse();

    private static ITypeSymbol GetParameterType(string parameterName)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(Source);
        var parameter = SyntaxNodeLocator.FindFirst<ParameterSyntax>(
            tree,
            candidate => string.Equals(candidate.Identifier.ValueText, parameterName, StringComparison.Ordinal)
        );

        var symbol = semanticModel.GetDeclaredSymbol(parameter) as IParameterSymbol;

        return symbol?.Type ?? throw new InvalidOperationException($"No parameter named '{parameterName}' found.");
    }
}
