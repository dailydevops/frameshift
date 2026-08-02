namespace NetEvolve.FrameShift.Tests.Unit.Mutations;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the argument guards every mutation operator inherits from <see cref="MutationOperatorBase" />
/// once for the whole registry instead of repeating the same three assertions in every operator test,
/// and pins the behaviour of the base class itself: constructor validation, the syntax kind filter and
/// the <c>CreateMutation</c> helper that composes the operator identifier.
/// </summary>
public class MutationOperatorGuardTests
{
    /// <summary>
    /// The syntax kind used to probe the filter of the base class. No registered operator supports a
    /// class declaration, which every test that uses it asserts before relying on it.
    /// </summary>
    private const SyntaxKind UnsupportedKind = SyntaxKind.ClassDeclaration;

    private const string ProbeId = "probe";

    private const string GuardSource = """
        namespace Fixtures;

        internal static class Guarded
        {
            internal static int Combine(int left, int right) => left + right;
        }
        """;

    /// <summary>
    /// Feeds every registered operator into the guard tests by its identifier, which keeps the public
    /// test signatures free of the internal operator interface.
    /// </summary>
    /// <returns>One factory per registered operator identifier.</returns>
    public static IEnumerable<Func<string>> RegisteredOperatorIds() =>
        MutationOperatorRegistry.All.Select(item => item.Id).Select(id => (Func<string>)(() => id));

    [Test]
    public async Task RegisteredOperatorIds_DataSource_CoversTheWholeRegistry()
    {
        var ids = RegisteredOperatorIds().Select(factory => factory()).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(ids).IsEquivalentTo(MutationOperatorRegistry.All.Select(item => item.Id).ToArray());
            _ = await Assert.That(ids.Length).IsEqualTo(MutationOperatorRegistry.All.Length);
        }
    }

    [Test]
    [MethodDataSource(nameof(RegisteredOperatorIds))]
    public async Task CreateMutations_NodeIsNull_ThrowsArgumentNullException(string operatorId)
    {
        var (_, semanticModel, _) = CompilationFactory.CreateWithModel(GuardSource);
        var mutationOperator = Find(operatorId);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutationOperator.CreateMutations(null!, semanticModel, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("node");
    }

    [Test]
    [MethodDataSource(nameof(RegisteredOperatorIds))]
    public async Task CreateMutations_SemanticModelIsNull_ThrowsArgumentNullException(string operatorId)
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutationOperator = Find(operatorId);
        var node = SyntaxNodeLocator.FindFirst<ClassDeclarationSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() =>
            mutationOperator.CreateMutations(node, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("semanticModel");
    }

    [Test]
    [MethodDataSource(nameof(RegisteredOperatorIds))]
    public async Task CreateMutations_UnsupportedSyntaxKind_ReturnsEmpty(string operatorId)
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutationOperator = Find(operatorId);
        var node = SyntaxNodeLocator.FindFirst<ClassDeclarationSyntax>(tree);

        var mutations = mutationOperator.CreateMutations(node, semanticModel, CancellationToken.None).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(node.Kind()).IsEqualTo(UnsupportedKind);
            _ = await Assert.That(mutationOperator.SupportedSyntaxKinds.Contains(UnsupportedKind)).IsFalse();
            _ = await Assert.That(mutations).IsEmpty();
        }
    }

    [Test]
    [MethodDataSource(nameof(RegisteredOperatorIds))]
    public async Task SupportedSyntaxKinds_EveryOperator_IsNeitherDefaultNorEmpty(string operatorId)
    {
        var mutationOperator = Find(operatorId);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutationOperator.SupportedSyntaxKinds.IsDefaultOrEmpty).IsFalse();
            _ = await Assert.That(mutationOperator.Id).IsEqualTo(operatorId);
        }
    }

    [Test]
    public async Task Constructor_IdIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = new ProbeMutator(null!, MutationKind.ArithmeticOperator, [SyntaxKind.AddExpression])
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("id");
    }

    [Test]
    public async Task Constructor_IdIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _ = new ProbeMutator(string.Empty, MutationKind.ArithmeticOperator, [SyntaxKind.AddExpression])
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
            _ = await Assert.That(exception.ParamName).IsEqualTo("id");
            _ = await Assert.That(exception.Message).Contains("The operator id must not be empty.");
        }
    }

    [Test]
    public async Task Constructor_SupportedSyntaxKindsIsDefault_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _ = new ProbeMutator(ProbeId, MutationKind.ArithmeticOperator, default)
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
            _ = await Assert.That(exception.ParamName).IsEqualTo("supportedSyntaxKinds");
            _ = await Assert.That(exception.Message).Contains("At least one supported syntax kind must be specified.");
        }
    }

    [Test]
    public async Task Constructor_SupportedSyntaxKindsIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            _ = new ProbeMutator(ProbeId, MutationKind.ArithmeticOperator, ImmutableArray<SyntaxKind>.Empty)
        );

        using (Assert.Multiple())
        {
            _ = await Assert.That(exception.GetType()).IsEqualTo(typeof(ArgumentException));
            _ = await Assert.That(exception.ParamName).IsEqualTo("supportedSyntaxKinds");
        }
    }

    [Test]
    public async Task Constructor_ValidArguments_ExposesIdKindAndSupportedKinds()
    {
        var mutator = new ProbeMutator(ProbeId, MutationKind.StringLiteral, [SyntaxKind.AddExpression]);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.Id).IsEqualTo(ProbeId);
            _ = await Assert.That(mutator.Kind).IsEqualTo(MutationKind.StringLiteral);
            _ = await Assert.That(mutator.SupportedSyntaxKinds).IsEquivalentTo([SyntaxKind.AddExpression]);
        }
    }

    [Test]
    public async Task CreateMutations_SupportedSyntaxKind_DelegatesToTheCore()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutator = new ProbeMutator(ProbeId, MutationKind.ArithmeticOperator, [SyntaxKind.AddExpression]);
        var node = SyntaxNodeLocator.FindFirst<BinaryExpressionSyntax>(tree);

        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutator.CoreInvocations).IsEqualTo(1);
            _ = await Assert.That(mutations.Length).IsEqualTo(1);
            _ = await Assert.That(mutations[0].OperatorId).IsEqualTo("probe.identity");
        }
    }

    [Test]
    public async Task CreateMutations_UnsupportedSyntaxKind_NeverReachesTheCore()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutator = new ProbeMutator(ProbeId, MutationKind.ArithmeticOperator, [SyntaxKind.AddExpression]);
        var node = SyntaxNodeLocator.FindFirst<ClassDeclarationSyntax>(tree);

        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToArray();

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutations).IsEmpty();
            _ = await Assert.That(mutator.CoreInvocations).IsEqualTo(0);
        }
    }

    [Test]
    public async Task CreateMutation_OperatorSuffixIsNull_ThrowsArgumentNullException()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutator = new ProbeMutator(ProbeId, MutationKind.ArithmeticOperator, [SyntaxKind.AddExpression]);
        var node = SyntaxNodeLocator.FindFirst<BinaryExpressionSyntax>(tree);

        var exception = Assert.Throws<ArgumentNullException>(() => mutator.Create(node, node, null!, "x => x"));

        _ = await Assert.That(exception.ParamName).IsEqualTo("operatorSuffix");
    }

    [Test]
    public async Task CreateMutation_ValidArguments_ComposesTheOperatorIdFromIdAndSuffix()
    {
        var (_, _, tree) = CompilationFactory.CreateWithModel(GuardSource);
        var mutator = new ProbeMutator(ProbeId, MutationKind.StringLiteral, [SyntaxKind.AddExpression]);
        var node = SyntaxNodeLocator.FindFirst<BinaryExpressionSyntax>(tree);

        var mutation = mutator.Create(node, node.Left, "add-to-left", "a + b => a");

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutation.OperatorId).IsEqualTo("probe.add-to-left");
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("a + b => a");
            _ = await Assert.That(mutation.Kind).IsEqualTo(MutationKind.StringLiteral);
            _ = await Assert.That(mutation.Original.ToString()).IsEqualTo(node.ToString());
            _ = await Assert.That(mutation.Replacement.ToString()).IsEqualTo(node.Left.ToString());
        }
    }

    private static IMutationOperator Find(string operatorId) =>
        MutationOperatorRegistry.All.Single(item => string.Equals(item.Id, operatorId, StringComparison.Ordinal));

    /// <summary>
    /// A minimal <see cref="MutationOperatorBase" /> implementation that makes the protected members of
    /// the base class reachable from a test and records whether the syntax kind filter let a node
    /// through.
    /// </summary>
    private sealed class ProbeMutator : MutationOperatorBase
    {
        public ProbeMutator(string id, MutationKind kind, ImmutableArray<SyntaxKind> supportedSyntaxKinds)
            : base(id, kind, supportedSyntaxKinds) { }

        /// <summary>
        /// Gets the number of times the syntax kind filter forwarded a node to the core implementation.
        /// </summary>
        public int CoreInvocations { get; private set; }

        public Mutation Create(
            SyntaxNode original,
            SyntaxNode replacement,
            string operatorSuffix,
            string displayName
        ) => CreateMutation(original, replacement, operatorSuffix, displayName);

        protected override IEnumerable<Mutation> CreateMutationsCore(
            SyntaxNode node,
            SemanticModel semanticModel,
            CancellationToken cancellationToken
        )
        {
            CoreInvocations++;

            return [CreateMutation(node, node, "identity", "x => x")];
        }
    }
}
