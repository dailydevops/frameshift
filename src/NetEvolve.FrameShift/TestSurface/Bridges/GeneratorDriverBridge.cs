namespace NetEvolve.FrameShift.TestSurface.Bridges;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;

/// <summary>
/// Recognises the well-known Roslyn source-generator test harness pattern, where a
/// <c>Microsoft.CodeAnalysis.GeneratorDriver</c> invokes <c>IIncrementalGenerator.Initialize</c> or
/// <c>ISourceGenerator.Initialize</c>/<c>Execute</c> from inside an external assembly. That call is
/// invisible to the ordinary reachability walk, which only ever sees the test's own syntax: the driver
/// itself performs the dispatch, in a compilation this analyzer never inspects. Without this bridge, the
/// only way to make such a generator's entry points reachable at all is an artificial delegate-reference
/// test, which is exactly the shape of test this analyzer wants to stop rewarding.
/// </summary>
/// <remarks>
/// The recognised shape is deliberately narrow: a fluent <c>XxxGeneratorDriver.Create(generators...)</c>
/// call, directly chained into <c>.RunGenerators(...)</c> or <c>.RunGeneratorsAndUpdateCompilation(...)</c>,
/// optionally wrapping a generator argument in <c>.AsSourceGenerator()</c>. A driver instance assembled
/// across several statements and stored in a local is not traced, because doing so would need a general
/// dataflow analysis this static pass does not perform. Missing such a shape only costs a reachability
/// gap that a human can still dismiss by hand; it never fabricates a bridge that is not there.
/// </remarks>
internal sealed class GeneratorDriverBridge : IInvocationBridge
{
    private const string IncrementalGeneratorInterfaceMetadataName = "Microsoft.CodeAnalysis.IIncrementalGenerator";
    private const string SourceGeneratorInterfaceMetadataName = "Microsoft.CodeAnalysis.ISourceGenerator";
    private const string GeneratorDriverBaseTypeMetadataName = "Microsoft.CodeAnalysis.GeneratorDriver";
    private const string CreateMethodName = "Create";
    private const string AsSourceGeneratorMethodName = "AsSourceGenerator";

    private static readonly string[] _runMethodNames = ["RunGenerators", "RunGeneratorsAndUpdateCompilation"];

    /// <summary>
    /// Gets the single instance of this bridge, which is stateless and therefore safe to share.
    /// </summary>
    public static readonly GeneratorDriverBridge Instance = new GeneratorDriverBridge();

    private GeneratorDriverBridge() { }

    /// <summary>
    /// The well-known types this bridge needs, resolved at most once per compilation. Every candidate
    /// invocation of a whole compilation shares one instance, so the type table is consulted three times
    /// total instead of once per invocation.
    /// </summary>
    public readonly struct Context
    {
        internal Context(
            INamedTypeSymbol? generatorDriverType,
            INamedTypeSymbol? incrementalGeneratorType,
            INamedTypeSymbol? sourceGeneratorType
        )
        {
            GeneratorDriverType = generatorDriverType;
            IncrementalGeneratorType = incrementalGeneratorType;
            SourceGeneratorType = sourceGeneratorType;
        }

        /// <summary>
        /// Gets a value indicating whether the compilation references both a generator driver type and at
        /// least one of the two generator interfaces, which is the precondition for this bridge to ever
        /// recognise anything at all. When this is <see langword="false" />, a caller can skip every
        /// per-invocation check entirely: a compilation that does not reference the Roslyn API can never
        /// contain the pattern this bridge looks for.
        /// </summary>
        public bool IsApplicable =>
            GeneratorDriverType is not null
            && (IncrementalGeneratorType is not null || SourceGeneratorType is not null);

        internal INamedTypeSymbol? GeneratorDriverType { get; }

        internal INamedTypeSymbol? IncrementalGeneratorType { get; }

        internal INamedTypeSymbol? SourceGeneratorType { get; }
    }

    /// <summary>
    /// Resolves the well-known types this bridge needs for <paramref name="compilation" />, through the
    /// shared <see cref="WellKnownTypeCache" /> so the metadata table is consulted at most once even
    /// across several callers of this bridge.
    /// </summary>
    /// <param name="compilation">The test compilation to resolve the types against.</param>
    /// <returns>The resolved context, see <see cref="Context.IsApplicable" />.</returns>
    public static Context Resolve(Compilation compilation) =>
        new Context(
            WellKnownTypeCache.GetType(compilation, GeneratorDriverBaseTypeMetadataName),
            WellKnownTypeCache.GetType(compilation, IncrementalGeneratorInterfaceMetadataName),
            WellKnownTypeCache.GetType(compilation, SourceGeneratorInterfaceMetadataName)
        );

    /// <summary>
    /// Finds the generator entry points that <paramref name="invocation" /> bridges to, if it is a call
    /// to <c>GeneratorDriver.RunGenerators</c> or <c>RunGeneratorsAndUpdateCompilation</c> on a driver
    /// created inline from one or more recognisable generator instances.
    /// </summary>
    /// <param name="semanticModel">The semantic model of the test method being walked.</param>
    /// <param name="invocation">The candidate invocation expression.</param>
    /// <param name="invokedMethod">
    /// The method symbol the caller already resolved for <paramref name="invocation" />, reused here
    /// instead of asking the binder to resolve it a second time.
    /// </param>
    /// <param name="context">The well-known types of the compilation, see <see cref="Resolve" />.</param>
    /// <param name="cancellationToken">A token observed while resolving symbols.</param>
    /// <returns>
    /// The <c>Initialize</c> and, for <see cref="Context.SourceGeneratorType" />, <c>Execute</c> methods
    /// the driver call is guaranteed to invoke; empty when <paramref name="invocation" /> does not match
    /// the recognised shape.
    /// </returns>
    public static IEnumerable<IMethodSymbol> FindBridgedMembers(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokedMethod,
        Context context,
        CancellationToken cancellationToken
    )
    {
        if (!_runMethodNames.Contains(invokedMethod.Name, StringComparer.Ordinal))
        {
            yield break;
        }

        if (
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || !DerivesFromGeneratorDriver(invokedMethod.ContainingType, context.GeneratorDriverType)
        )
        {
            yield break;
        }

        foreach (var generatorExpression in FindGeneratorArguments(memberAccess.Expression))
        {
            foreach (
                var entryPoint in FindGeneratorEntryPoints(
                    semanticModel,
                    generatorExpression,
                    context,
                    cancellationToken
                )
            )
            {
                yield return entryPoint;
            }
        }
    }

    /// <inheritdoc />
    object? IInvocationBridge.CreateContext(Compilation compilation) => Resolve(compilation);

    /// <inheritdoc />
    bool IInvocationBridge.IsApplicable(object? context) => context is Context { IsApplicable: true };

    /// <inheritdoc />
    IEnumerable<IMethodSymbol> IInvocationBridge.FindBridgedMembers(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokedMethod,
        object? context,
        CancellationToken cancellationToken
    ) =>
        context is Context typedContext
            ? FindBridgedMembers(semanticModel, invocation, invokedMethod, typedContext, cancellationToken)
            : [];

    private static bool DerivesFromGeneratorDriver(INamedTypeSymbol? type, INamedTypeSymbol? generatorDriverType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, generatorDriverType))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the generator instance expressions passed to an inline <c>XxxGeneratorDriver.Create(...)</c>
    /// call feeding <paramref name="driverExpression" />.
    /// </summary>
    /// <param name="driverExpression">The expression the recognised <c>RunGenerators</c> call is on.</param>
    private static IEnumerable<ExpressionSyntax> FindGeneratorArguments(ExpressionSyntax driverExpression)
    {
        if (
            driverExpression
            is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: CreateMethodName },
                ArgumentList.Arguments: var arguments,
            }
        )
        {
            yield break;
        }

        foreach (var argument in arguments)
        {
            foreach (var generatorExpression in FlattenGeneratorExpression(argument.Expression))
            {
                yield return generatorExpression;
            }
        }
    }

    /// <summary>
    /// Unwraps the shapes a generator argument commonly takes: an inline array of several generators, or
    /// a single generator wrapped in <c>.AsSourceGenerator()</c>.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> FlattenGeneratorExpression(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ArrayCreationExpressionSyntax { Initializer: { } explicitArray }:
                foreach (var element in explicitArray.Expressions)
                {
                    // Every array element can itself be wrapped in ".AsSourceGenerator()", so it goes
                    // through the same unwrapping the top-level argument does, instead of only the outer
                    // array shape being recognised.
                    foreach (var flattened in FlattenGeneratorExpression(element))
                    {
                        yield return flattened;
                    }
                }

                break;

            case ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitArray }:
                foreach (var element in implicitArray.Expressions)
                {
                    foreach (var flattened in FlattenGeneratorExpression(element))
                    {
                        yield return flattened;
                    }
                }

                break;

            case InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: AsSourceGeneratorMethodName,
                } asSourceGeneratorAccess,
            }:
                yield return asSourceGeneratorAccess.Expression;
                break;

            default:
                yield return expression;
                break;
        }
    }

    /// <summary>
    /// Resolves the static type of a generator instance expression and returns the concrete
    /// implementations of the generator entry points that type is guaranteed to expose.
    /// </summary>
    private static IEnumerable<IMethodSymbol> FindGeneratorEntryPoints(
        SemanticModel semanticModel,
        ExpressionSyntax generatorExpression,
        Context context,
        CancellationToken cancellationToken
    )
    {
        if (semanticModel.GetTypeInfo(generatorExpression, cancellationToken).Type is not INamedTypeSymbol type)
        {
            yield break;
        }

        foreach (var generatorInterface in type.AllInterfaces)
        {
            if (
                !SymbolEqualityComparer.Default.Equals(generatorInterface, context.IncrementalGeneratorType)
                && !SymbolEqualityComparer.Default.Equals(generatorInterface, context.SourceGeneratorType)
            )
            {
                continue;
            }

            foreach (var interfaceMember in generatorInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (type.FindImplementationForInterfaceMember(interfaceMember) is IMethodSymbol implementation)
                {
                    yield return implementation;
                }
            }
        }
    }
}
