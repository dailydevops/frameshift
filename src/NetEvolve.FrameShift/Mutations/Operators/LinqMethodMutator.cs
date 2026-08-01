namespace NetEvolve.FrameShift.Mutations.Operators;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Mutates a call to a well known <c>System.Linq.Enumerable</c> method into its counterpart, along the
/// pairs <c>All</c> / <c>Any</c>, <c>First</c> / <c>FirstOrDefault</c>, <c>Single</c> /
/// <c>SingleOrDefault</c>, <c>Last</c> / <c>LastOrDefault</c>, <c>OrderBy</c> / <c>OrderByDescending</c>,
/// <c>ThenBy</c> / <c>ThenByDescending</c>, <c>Min</c> / <c>Max</c>, <c>MinBy</c> / <c>MaxBy</c>,
/// <c>Skip</c> / <c>Take</c>, <c>Skip</c> / <c>SkipLast</c> and <c>SkipWhile</c> / <c>TakeWhile</c>. A
/// query that decides correctness through one of these choices has no mutant pinning it down until this
/// operator renames the call.
/// </summary>
/// <remarks>
/// <para>
/// The rename keeps whatever argument list the call already has - the receiver, the arguments, the
/// parentheses and all of their trivia are reused unchanged, only the name inside the member access is
/// replaced. That is a pure rename and, for <c>All</c> / <c>Any</c> in particular, deliberately not a
/// behaviour preserving one: <c>Any(predicate)</c> and <c>All(predicate)</c> read the same predicate but
/// answer opposite questions, which is exactly the point of a mutant a test suite is meant to catch. What
/// keeps the mutant compiling is that <c>Any</c> and <c>All</c> both declare a one argument predicate
/// overload with the same shape, not that the two calls behave alike.
/// </para>
/// <para>
/// Because the rename never touches the argument list, it only produces a mutant that compiles where the
/// target method declares an overload of the same shape: the same number of arguments, and a delegate
/// argument only where the source method also takes a delegate there. That is why <c>Any()</c> is not
/// renamed to <c>All()</c> - <c>All</c> has no parameterless overload - while <c>Any(predicate)</c> is
/// renamed to <c>All(predicate)</c>, and why <c>FirstOrDefault(defaultValue)</c> is not renamed to
/// <c>First(defaultValue)</c> - <c>First</c> has no overload taking a plain default value, only a
/// predicate - while <c>FirstOrDefault(predicate)</c> is renamed to <c>First(predicate)</c>. The check
/// only compares the shape of the parameter list, not the exact generic type, so it does not reimplement
/// overload resolution; where the shape looks compatible but the mutant still fails to bind,
/// <c>MutantCompiler</c> discards it.
/// </para>
/// <para>
/// The invoked method has to be bound to <c>System.Linq.Enumerable</c> itself, so a same-named,
/// same-shaped method on a user-defined type - an extension method or an ordinary instance method
/// declared elsewhere - is never touched. The call is renamed whether it is written as an extension
/// method call (<c>x.First()</c>) or as a static call (<c>Enumerable.First(x)</c>), because both forms
/// are a <see cref="MemberAccessExpressionSyntax" /> naming the same bound method; only the identifier of
/// that member access is replaced.
/// </para>
/// </remarks>
internal sealed class LinqMethodMutator : MutationOperatorBase
{
    private const string AllName = "All";
    private const string AnyName = "Any";
    private const string FirstName = "First";
    private const string FirstOrDefaultName = "FirstOrDefault";
    private const string SingleName = "Single";
    private const string SingleOrDefaultName = "SingleOrDefault";
    private const string LastName = "Last";
    private const string LastOrDefaultName = "LastOrDefault";
    private const string OrderByName = "OrderBy";
    private const string OrderByDescendingName = "OrderByDescending";
    private const string ThenByName = "ThenBy";
    private const string ThenByDescendingName = "ThenByDescending";
    private const string MinName = "Min";
    private const string MaxName = "Max";
    private const string MinByName = "MinBy";
    private const string MaxByName = "MaxBy";
    private const string SkipName = "Skip";
    private const string TakeName = "Take";
    private const string SkipLastName = "SkipLast";
    private const string SkipWhileName = "SkipWhile";
    private const string TakeWhileName = "TakeWhile";

    private const string EnumerableMetadataName = "System.Linq.Enumerable";

    private static readonly ImmutableArray<SyntaxKind> _supportedSyntaxKinds = [SyntaxKind.InvocationExpression];

    private static readonly ImmutableArray<string> _fromAll = [AnyName];
    private static readonly ImmutableArray<string> _fromAny = [AllName];
    private static readonly ImmutableArray<string> _fromFirst = [FirstOrDefaultName];
    private static readonly ImmutableArray<string> _fromFirstOrDefault = [FirstName];
    private static readonly ImmutableArray<string> _fromSingle = [SingleOrDefaultName];
    private static readonly ImmutableArray<string> _fromSingleOrDefault = [SingleName];
    private static readonly ImmutableArray<string> _fromLast = [LastOrDefaultName];
    private static readonly ImmutableArray<string> _fromLastOrDefault = [LastName];
    private static readonly ImmutableArray<string> _fromOrderBy = [OrderByDescendingName];
    private static readonly ImmutableArray<string> _fromOrderByDescending = [OrderByName];
    private static readonly ImmutableArray<string> _fromThenBy = [ThenByDescendingName];
    private static readonly ImmutableArray<string> _fromThenByDescending = [ThenByName];
    private static readonly ImmutableArray<string> _fromMin = [MaxName];
    private static readonly ImmutableArray<string> _fromMax = [MinName];
    private static readonly ImmutableArray<string> _fromMinBy = [MaxByName];
    private static readonly ImmutableArray<string> _fromMaxBy = [MinByName];
    private static readonly ImmutableArray<string> _fromSkip = [TakeName, SkipLastName];
    private static readonly ImmutableArray<string> _fromTake = [SkipName];
    private static readonly ImmutableArray<string> _fromSkipLast = [SkipName];
    private static readonly ImmutableArray<string> _fromSkipWhile = [TakeWhileName];
    private static readonly ImmutableArray<string> _fromTakeWhile = [SkipWhileName];

    /// <summary>
    /// Initializes a new instance of the <see cref="LinqMethodMutator" /> class.
    /// </summary>
    public LinqMethodMutator()
        : base("linq.method", MutationKind.LinqMethod, _supportedSyntaxKinds) { }

    /// <inheritdoc />
    protected override IEnumerable<Mutation> CreateMutationsCore(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // MutationOperatorBase.CreateMutations only hands over nodes of one of the SupportedSyntaxKinds, and
        // the only supported kind is the invocation expression, so the cast cannot fail.
        var invocation = (InvocationExpressionSyntax)node;

        if (
            invocation.Expression is not MemberAccessExpressionSyntax access
            || !access.IsKind(SyntaxKind.SimpleMemberAccessExpression)
        )
        {
            return [];
        }

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
        {
            return [];
        }

        var enumerableType = WellKnownTypeCache.GetType(semanticModel.Compilation, EnumerableMetadataName);

        if (enumerableType is null || !SymbolEqualityComparer.Default.Equals(method.ContainingType, enumerableType))
        {
            return [];
        }

        var targets = GetTargets(method.Name);

        if (targets.IsEmpty)
        {
            return [];
        }

        return CreateRenames(invocation, access, method, targets, enumerableType, cancellationToken);
    }

    /// <summary>
    /// Yields one mutation per counterpart of the called method whose parameter list has the same shape,
    /// so that every produced mutant compiles.
    /// </summary>
    /// <param name="invocation">The call that gets replaced.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="method">The bound symbol of the called method.</param>
    /// <param name="targets">The candidate counterpart names of the called method.</param>
    /// <param name="enumerableType">The <c>System.Linq.Enumerable</c> type of the analysed compilation.</param>
    /// <param name="cancellationToken">A token to observe while creating the mutations.</param>
    /// <returns>The mutations of the call, one per compatible counterpart.</returns>
    private IEnumerable<Mutation> CreateRenames(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        IMethodSymbol method,
        ImmutableArray<string> targets,
        INamedTypeSymbol enumerableType,
        CancellationToken cancellationToken
    )
    {
        foreach (var targetName in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasCompatibleOverload(enumerableType, targetName, method))
            {
                continue;
            }

            yield return CreateMutation(
                invocation,
                Rewrite(invocation, access, targetName),
                $"{GetSuffix(method.Name)}-to-{GetSuffix(targetName)}",
                $"{method.Name} => {targetName}"
            );
        }
    }

    /// <summary>
    /// Renames the called method, keeping the receiver, the argument list and all of their trivia exactly
    /// as they were written.
    /// </summary>
    /// <param name="invocation">The call to rewrite.</param>
    /// <param name="access">The member access naming the called method.</param>
    /// <param name="targetName">The name the call gets.</param>
    /// <returns>The rewritten call.</returns>
    private static InvocationExpressionSyntax Rewrite(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax access,
        string targetName
    )
    {
        var identifier = access.Name.Identifier;
        var name = SyntaxFactory.IdentifierName(
            SyntaxFactory.Identifier(identifier.LeadingTrivia, targetName, identifier.TrailingTrivia)
        );

        return invocation.WithExpression(access.WithName(name));
    }

    /// <summary>
    /// Decides whether <paramref name="enumerableType" /> declares a <paramref name="targetName" /> method
    /// whose parameter list, other than the source, has the same shape as <paramref name="method" />'s:
    /// the same number of parameters, and a delegate parameter only where <paramref name="method" /> also
    /// has one at that position.
    /// </summary>
    /// <param name="enumerableType">The <c>System.Linq.Enumerable</c> type of the analysed compilation.</param>
    /// <param name="targetName">The candidate counterpart name.</param>
    /// <param name="method">The bound symbol of the called method.</param>
    /// <returns><see langword="true" /> if a compatible overload exists.</returns>
    private static bool HasCompatibleOverload(INamedTypeSymbol enumerableType, string targetName, IMethodSymbol method)
    {
        var sourceParameters = GetSourceParameters(method);

        foreach (var candidate in enumerableType.GetMembers(targetName))
        {
            if (candidate is IMethodSymbol candidateMethod && HasMatchingShape(sourceParameters, candidateMethod))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the parameters of <paramref name="method" /> other than the source sequence, regardless of
    /// whether the call was written as an extension method call - whose bound symbol already excludes the
    /// source - or as a static call, whose bound symbol still declares it as the first parameter.
    /// </summary>
    /// <param name="method">The bound symbol of the called method.</param>
    /// <returns>The parameters after the source sequence.</returns>
    private static ImmutableArray<IParameterSymbol> GetSourceParameters(IMethodSymbol method)
    {
        var unreduced = method.ReducedFrom ?? method;

        return unreduced.Parameters.RemoveAt(0);
    }

    /// <summary>
    /// Decides whether the parameters of <paramref name="candidate" /> after its source sequence match
    /// <paramref name="sourceParameters" /> in shape: the same count, and a delegate at the same positions.
    /// </summary>
    /// <param name="sourceParameters">The parameters of the called method, after its source sequence.</param>
    /// <param name="candidate">A method of <c>System.Linq.Enumerable</c> sharing the candidate name.</param>
    /// <returns><see langword="true" /> if the shapes match.</returns>
    private static bool HasMatchingShape(ImmutableArray<IParameterSymbol> sourceParameters, IMethodSymbol candidate)
    {
        var candidateParameters = candidate.Parameters.RemoveAt(0);

        if (candidateParameters.Length != sourceParameters.Length)
        {
            return false;
        }

        for (var index = 0; index < sourceParameters.Length; index++)
        {
            if (IsDelegate(sourceParameters[index].Type) != IsDelegate(candidateParameters[index].Type))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides whether <paramref name="type" /> is a delegate type, which distinguishes a predicate or
    /// selector parameter from a plain value or comparer parameter of the same arity.
    /// </summary>
    /// <param name="type">The declared type of a parameter.</param>
    /// <returns><see langword="true" /> if the parameter is a delegate.</returns>
    private static bool IsDelegate(ITypeSymbol type) => type.TypeKind == TypeKind.Delegate;

    /// <summary>
    /// Gets the counterparts of a well known <c>System.Linq.Enumerable</c> method, or an empty array if
    /// the name is none of them.
    /// </summary>
    /// <param name="methodName">The name of the called method.</param>
    /// <returns>The candidate counterpart names.</returns>
    private static ImmutableArray<string> GetTargets(string methodName) =>
        methodName switch
        {
            AllName => _fromAll,
            AnyName => _fromAny,
            FirstName => _fromFirst,
            FirstOrDefaultName => _fromFirstOrDefault,
            SingleName => _fromSingle,
            SingleOrDefaultName => _fromSingleOrDefault,
            LastName => _fromLast,
            LastOrDefaultName => _fromLastOrDefault,
            OrderByName => _fromOrderBy,
            OrderByDescendingName => _fromOrderByDescending,
            ThenByName => _fromThenBy,
            ThenByDescendingName => _fromThenByDescending,
            MinName => _fromMin,
            MaxName => _fromMax,
            MinByName => _fromMinBy,
            MaxByName => _fromMaxBy,
            SkipName => _fromSkip,
            TakeName => _fromTake,
            SkipLastName => _fromSkipLast,
            SkipWhileName => _fromSkipWhile,
            TakeWhileName => _fromTakeWhile,
            _ => [],
        };

    /// <summary>
    /// Gets the identifier fragment a method name contributes to the operator id.
    /// </summary>
    /// <param name="methodName">The name of one of the well known methods.</param>
    /// <returns>The fragment, for example <c>first-or-default</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="methodName" /> is none of them.</exception>
    private static string GetSuffix(string methodName) =>
        methodName switch
        {
            AllName => "all",
            AnyName => "any",
            FirstName => "first",
            FirstOrDefaultName => "first-or-default",
            SingleName => "single",
            SingleOrDefaultName => "single-or-default",
            LastName => "last",
            LastOrDefaultName => "last-or-default",
            OrderByName => "order-by",
            OrderByDescendingName => "order-by-descending",
            ThenByName => "then-by",
            ThenByDescendingName => "then-by-descending",
            MinName => "min",
            MaxName => "max",
            MinByName => "min-by",
            MaxByName => "max-by",
            SkipName => "skip",
            TakeName => "take",
            SkipLastName => "skip-last",
            SkipWhileName => "skip-while",
            TakeWhileName => "take-while",
            _ => throw new ArgumentOutOfRangeException(
                nameof(methodName),
                methodName,
                "The method is not one of the well known System.Linq.Enumerable methods this operator knows."
            ),
        };
}
