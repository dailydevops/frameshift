namespace NetEvolve.FrameShift.Tests.Unit.Mutations;

using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Mutations;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Guards the registry as the single source of truth for the mutation operators: which operators exist,
/// that their identifiers stay unique and that the syntax kind index agrees with the operators themselves.
/// </summary>
public class MutationOperatorRegistryTests
{
    private static readonly string[] _expectedOperatorIds =
    [
        "arithmetic",
        "arithmetic-assignment",
        "bitwise",
        "boolean-literal",
        "conditional-expression",
        "equality",
        "increment-decrement",
        "logical",
        "negation",
        "null-coalescing",
        "numeric-literal",
        "relational",
        "string-literal",
        "unary",
    ];

    [Test]
    public async Task All_Registry_ContainsExactlyTheRegisteredOperators()
    {
        var actual = MutationOperatorRegistry.All.Select(mutationOperator => mutationOperator.Id);

        _ = await Assert.That(MutationOperatorRegistry.All.Length).IsEqualTo(_expectedOperatorIds.Length);
        _ = await Assert.That(Join(Sort(actual))).IsEqualTo(Join(_expectedOperatorIds));
    }

    [Test]
    public async Task All_Registry_UsesUniqueOperatorIds()
    {
        var ids = MutationOperatorRegistry.All.Select(mutationOperator => mutationOperator.Id).ToList();

        _ = await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(ids.Count);
    }

    [Test]
    public async Task All_Registry_UsesUniqueOperatorInstancesPerType()
    {
        var types = MutationOperatorRegistry.All.Select(mutationOperator => mutationOperator.GetType()).ToList();

        _ = await Assert.That(types.Distinct().Count()).IsEqualTo(types.Count);
    }

    [Test]
    public async Task All_EveryOperator_SupportsAtLeastOneSyntaxKind()
    {
        var offenders = MutationOperatorRegistry
            .All.Where(mutationOperator => mutationOperator.SupportedSyntaxKinds.IsDefaultOrEmpty)
            .Select(mutationOperator => mutationOperator.Id);

        _ = await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task ForSyntaxKind_EverySyntaxKind_AgreesWithTheOperators()
    {
        var mismatches = Enum.GetValues<SyntaxKind>().Where(IsMismatch).Select(kind => kind.ToString());

        _ = await Assert.That(mismatches).IsEmpty();
    }

    [Test]
    public async Task ForSyntaxKind_UnsupportedSyntaxKind_ReturnsEmpty()
    {
        var operators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.ClassDeclaration);

        _ = await Assert.That(operators.Length).IsEqualTo(0);
        _ = await Assert
            .That(MutationOperatorRegistry.SupportedSyntaxKinds.Contains(SyntaxKind.ClassDeclaration))
            .IsFalse();
    }

    [Test]
    public async Task ForSyntaxKind_SupportedSyntaxKind_ReturnsTheOwningOperators()
    {
        var booleanOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.TrueLiteralExpression);
        var conditionalOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.ConditionalExpression);

        _ = await Assert.That(Join(Sort(booleanOperators.Select(item => item.Id)))).IsEqualTo("boolean-literal");
        _ = await Assert
            .That(Join(Sort(conditionalOperators.Select(item => item.Id))))
            .IsEqualTo("conditional-expression, negation");
    }

    [Test]
    public async Task SupportedSyntaxKinds_Registry_IsTheDistinctUnionOfTheOperators()
    {
        var expected = MutationOperatorRegistry
            .All.SelectMany(mutationOperator => mutationOperator.SupportedSyntaxKinds)
            .Distinct()
            .OrderBy(kind => (int)kind)
            .Select(kind => kind.ToString());
        var actual = MutationOperatorRegistry.SupportedSyntaxKinds.Select(kind => kind.ToString());

        _ = await Assert.That(Join(actual)).IsEqualTo(Join(expected));
    }

    [Test]
    public async Task SupportedSyntaxKinds_Registry_ContainsNoDuplicate()
    {
        var kinds = MutationOperatorRegistry.SupportedSyntaxKinds;

        _ = await Assert.That(kinds.Distinct().Count()).IsEqualTo(kinds.Length);
        _ = await Assert.That(kinds.Length).IsGreaterThan(0);
    }

    private static bool IsMismatch(SyntaxKind kind)
    {
        var expected = MutationOperatorRegistry
            .All.Where(mutationOperator => mutationOperator.SupportedSyntaxKinds.Contains(kind))
            .Select(mutationOperator => mutationOperator.Id);
        var actual = MutationOperatorRegistry.ForSyntaxKind(kind).Select(mutationOperator => mutationOperator.Id);

        return !string.Equals(Join(Sort(expected)), Join(Sort(actual)), StringComparison.Ordinal);
    }

    private static IEnumerable<string> Sort(IEnumerable<string> values) =>
        values.OrderBy(value => value, StringComparer.Ordinal);

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);
}
