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
    /// <summary>
    /// The number of operators the registry must expose: the twenty expression operators, the string
    /// method operator, the <c>Math</c> method operator, the six operators of the culture sensitivity
    /// family, the eight operators of the regular expression pattern family, the LINQ method operator and
    /// the optional argument removal operator.
    /// </summary>
    private const int ExpectedOperatorCount = 37;

    private static readonly string[] _expectedOperatorIds =
    [
        "argument.optional-removal",
        "arithmetic",
        "arithmetic-assignment",
        "bitwise",
        "bitwise-assignment",
        "boolean-literal",
        "checked-context",
        "collection-initializer",
        "conditional-expression",
        "culture.case-conversion",
        "culture.culture-info",
        "culture.format-provider",
        "culture.regex-options",
        "culture.string-comparer",
        "culture.string-comparison",
        "equality",
        "increment-decrement",
        "linq.method",
        "logical",
        "math.method",
        "negation",
        "null-coalescing",
        "nullable-literal",
        "numeric-literal",
        "regex.alternation",
        "regex.anchor",
        "regex.backreference",
        "regex.character-class",
        "regex.escape",
        "regex.group",
        "regex.lookaround",
        "regex.quantifier",
        "relational",
        "statement-removal",
        "string-literal",
        "string-method",
        "unary",
    ];

    [Test]
    public async Task All_Registry_ContainsExactlyTheRegisteredOperators()
    {
        var actual = MutationOperatorRegistry.All.Select(mutationOperator => mutationOperator.Id);

        using (Assert.Multiple())
        {
            _ = await Assert.That(_expectedOperatorIds.Length).IsEqualTo(ExpectedOperatorCount);
            _ = await Assert.That(MutationOperatorRegistry.All.Length).IsEqualTo(ExpectedOperatorCount);
            _ = await Assert.That(Join(Sort(actual))).IsEqualTo(Join(_expectedOperatorIds));
        }
    }

    [Test]
    public async Task All_Registry_ListsTheOperatorIdsInAscendingOrdinalOrder() =>
        _ = await Assert.That(Join(_expectedOperatorIds)).IsEqualTo(Join(Sort(_expectedOperatorIds)));

    [Test]
    public async Task All_EveryOperator_ReportsAKnownMutationKind()
    {
        var kinds = Enum.GetValues<MutationKind>();
        var offenders = MutationOperatorRegistry
            .All.Where(mutationOperator => !kinds.Contains(mutationOperator.Kind))
            .Select(mutationOperator => mutationOperator.Id);

        _ = await Assert.That(offenders).IsEmpty();
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(operators.Length).IsEqualTo(0);
            _ = await Assert
                .That(MutationOperatorRegistry.SupportedSyntaxKinds.Contains(SyntaxKind.ClassDeclaration))
                .IsFalse();
        }
    }

    [Test]
    public async Task ForSyntaxKind_SupportedSyntaxKind_ReturnsTheOwningOperators()
    {
        var booleanOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.TrueLiteralExpression);
        var conditionalOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.ConditionalExpression);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Join(Sort(booleanOperators.Select(item => item.Id))))
                .IsEqualTo("boolean-literal, nullable-literal");
            _ = await Assert
                .That(Join(Sort(conditionalOperators.Select(item => item.Id))))
                .IsEqualTo("conditional-expression, negation");
        }
    }

    /// <summary>
    /// A string literal is claimed by the plain string literal operator and by every operator of the
    /// regular expression pattern family, because a pattern is spelled out as a string literal and the
    /// family cannot know from the syntax kind alone whether a literal is one.
    /// </summary>
    [Test]
    public async Task ForSyntaxKind_SharedSyntaxKind_ReturnsEveryClaimingOperator()
    {
        var memberAccessOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.SimpleMemberAccessExpression);
        var invocationOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.InvocationExpression);
        var stringLiteralOperators = MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.StringLiteralExpression);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Join(Sort(memberAccessOperators.Select(item => item.Id))))
                .IsEqualTo(
                    "culture.culture-info, culture.regex-options, culture.string-comparer, culture.string-comparison"
                );
            _ = await Assert
                .That(Join(Sort(invocationOperators.Select(item => item.Id))))
                .IsEqualTo("culture.case-conversion, culture.format-provider, linq.method, math.method, string-method");
            _ = await Assert
                .That(Join(Sort(stringLiteralOperators.Select(item => item.Id))))
                .IsEqualTo(
                    "regex.alternation, regex.anchor, regex.backreference, regex.character-class, regex.escape, regex.group, regex.lookaround, regex.quantifier, string-literal"
                );
        }
    }

    [Test]
    public async Task SupportedSyntaxKinds_SharedSyntaxKind_IsListedExactlyOnce()
    {
        var kinds = MutationOperatorRegistry.SupportedSyntaxKinds;

        using (Assert.Multiple())
        {
            _ = await Assert.That(kinds.Count(kind => kind == SyntaxKind.SimpleMemberAccessExpression)).IsEqualTo(1);
            _ = await Assert.That(kinds.Count(kind => kind == SyntaxKind.InvocationExpression)).IsEqualTo(1);
            _ = await Assert.That(kinds.Count(kind => kind == SyntaxKind.StringLiteralExpression)).IsEqualTo(1);
            _ = await Assert
                .That(MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.SimpleMemberAccessExpression).Length)
                .IsGreaterThan(1);
            _ = await Assert
                .That(MutationOperatorRegistry.ForSyntaxKind(SyntaxKind.StringLiteralExpression).Length)
                .IsGreaterThan(1);
        }
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

        using (Assert.Multiple())
        {
            _ = await Assert.That(kinds.Distinct().Count()).IsEqualTo(kinds.Length);
            _ = await Assert.That(kinds.Length).IsGreaterThan(0);
        }
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
