namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using System.Collections.Immutable;
using System.Globalization;
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
/// Covers <see cref="OptionalArgumentRemovalMutator" />, which drops a trailing comparer argument of an
/// object creation so that the call resolves to a same-type overload with no comparer at all.
/// </summary>
public class OptionalArgumentRemovalMutatorTests
{
    private const string OperatorId = "argument.optional-removal";

    private const string RemoveId = "argument.optional-removal.remove";

    /// <summary>
    /// A <c>Dictionary&lt;TKey, TValue&gt;</c> constructed with an explicit <c>StringComparer</c>, whose
    /// parameterless overload exists.
    /// </summary>
    private const string DictionaryStringComparerSource = """
        using System;
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static Dictionary<string, string> Create() =>
                    /*!*/new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
        """;

    private const string DictionaryStringComparerExpected = """
        using System;
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static Dictionary<string, string> Create() =>
                    /*!*/new Dictionary<string, string>();
            }
        }
        """;

    /// <summary>
    /// A <c>HashSet&lt;T&gt;</c> constructed with an explicit <c>IEqualityComparer&lt;T&gt;</c>, whose
    /// parameterless overload exists.
    /// </summary>
    private const string HashSetEqualityComparerSource = """
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static HashSet<string> Create() =>
                    /*!*/new HashSet<string>(EqualityComparer<string>.Default);
            }
        }
        """;

    private const string HashSetEqualityComparerExpected = """
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static HashSet<string> Create() =>
                    /*!*/new HashSet<string>();
            }
        }
        """;

    /// <summary>
    /// A <c>Dictionary&lt;TKey, TValue&gt;</c> constructed with a capacity argument in front of the
    /// comparer, whose overload without the comparer - but keeping the capacity - exists.
    /// </summary>
    private const string DictionaryCapacityAndComparerSource = """
        using System;
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static Dictionary<string, string> Create() =>
                    /*!*/new Dictionary<string, string>(16, StringComparer.Ordinal);
            }
        }
        """;

    private const string DictionaryCapacityAndComparerExpected = """
        using System;
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static Dictionary<string, string> Create() =>
                    /*!*/new Dictionary<string, string>(16);
            }
        }
        """;

    /// <summary>
    /// A custom type whose only constructor requires the comparer, so no argument-less overload exists.
    /// </summary>
    private const string OnlyComparerConstructorSource = """
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal sealed class OnlyComparerCtor
            {
                public OnlyComparerCtor(IComparer<int> comparer) { }
            }

            internal static class Factory
            {
                public static OnlyComparerCtor Create() =>
                    /*!*/new OnlyComparerCtor(Comparer<int>.Default);
            }
        }
        """;

    /// <summary>
    /// A custom type whose only constructor requires both a leading, unrelated argument and the trailing
    /// comparer. Dropping the comparer would leave a call with too few arguments for the only overload,
    /// which is the positional-and-required case the operator must not touch.
    /// </summary>
    private const string RequiresBothArgumentsSource = """
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal sealed class RequiresBoth
            {
                public RequiresBoth(int capacity, IComparer<int> comparer) { }
            }

            internal static class Factory
            {
                public static RequiresBoth Create() =>
                    /*!*/new RequiresBoth(4, Comparer<int>.Default);
            }
        }
        """;

    /// <summary>
    /// The comparer passed by name. Dropping a named argument could require reordering the arguments
    /// around it, which this first iteration deliberately does not attempt.
    /// </summary>
    private const string NamedComparerSource = """
        using System;
        using System.Collections.Generic;

        namespace Fixtures
        {
            internal static class Factory
            {
                public static Dictionary<string, string> Create() =>
                    /*!*/new Dictionary<string, string>(comparer: StringComparer.Ordinal);
            }
        }
        """;

    /// <summary>
    /// A trailing argument of a plain, non comparer shaped type, which is out of scope for this first
    /// iteration.
    /// </summary>
    private const string NonComparerArgumentSource = """
        namespace Fixtures
        {
            internal sealed class Widget
            {
                public Widget() { }

                public Widget(int size) { }
            }

            internal static class Factory
            {
                public static Widget Create() => /*!*/new Widget(5);
            }
        }
        """;

    /// <summary>
    /// An object creation without any argument list at all, such as <c>new object()</c> written as
    /// <c>new object</c> in modern C#, or a plain parameterless call. Nothing can be dropped.
    /// </summary>
    private const string NoArgumentsSource = """
        namespace Fixtures
        {
            internal static class Factory
            {
                public static object Create() => /*!*/new object();
            }
        }
        """;

    private static readonly OptionalArgumentRemovalMutator _mutator = new OptionalArgumentRemovalMutator();

    [Test]
    public async Task SupportedSyntaxKinds_Always_IsTheObjectCreationKind()
    {
        SyntaxKind[] expected = [SyntaxKind.ObjectCreationExpression];

        _ = await Assert.That(_mutator.SupportedSyntaxKinds.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Metadata_Always_IdentifiesTheOptionalArgumentRemovalFamily()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(_mutator.Id).IsEqualTo(OperatorId);
            _ = await Assert.That(_mutator.Kind).IsEqualTo(MutationKind.OptionalArgumentRemoval);
        }
    }

    [Test]
    public async Task CreateMutations_StringComparerArgumentWithParameterlessOverload_RemovesIt()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, _, errors) = Mutate(DictionaryStringComparerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert.That(mutations.Single().Kind).IsEqualTo(MutationKind.OptionalArgumentRemoval);
            _ = await Assert
                .That(mutations.Single().ApplyTo(tree).ToString())
                .IsEqualTo(DictionaryStringComparerExpected);
        }
    }

    [Test]
    public async Task ApplyTo_StringComparerRemoved_ProducesCompilableSource()
    {
        var (mutations, _, tree, _, _) = Mutate(DictionaryStringComparerSource);
        var mutated = mutations.Single().ApplyTo(tree).ToString();
        var compilation = CompilationFactory.Create(mutated);

        using (Assert.Multiple())
        {
            _ = await Assert.That(mutated).Contains("new Dictionary<string, string>()");
            _ = await Assert.That(Describe(CompilationFactory.GetCompileErrors(compilation))).IsEqualTo(string.Empty);
        }
    }

    [Test]
    public async Task CreateMutations_EqualityComparerArgumentWithParameterlessOverload_RemovesIt()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, _, errors) = Mutate(HashSetEqualityComparerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert
                .That(mutations.Single().ApplyTo(tree).ToString())
                .IsEqualTo(HashSetEqualityComparerExpected);
        }
    }

    [Test]
    public async Task CreateMutations_ComparerBehindAnUnrelatedLeadingArgument_RemovesOnlyTheComparer()
    {
        string[] expectedIds = [RemoveId];
        var (mutations, _, tree, _, errors) = Mutate(DictionaryCapacityAndComparerSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(OperatorIds(mutations)).IsEquivalentTo(expectedIds);
            _ = await Assert
                .That(mutations.Single().ApplyTo(tree).ToString())
                .IsEqualTo(DictionaryCapacityAndComparerExpected);
        }
    }

    [Test]
    public async Task CreateMutations_ProvidedArgumentReportsTheRemovedArgumentAsTheLocation()
    {
        var (mutations, _, tree, _, errors) = Mutate(DictionaryStringComparerSource);
        var objectCreation = SyntaxNodeLocator.FindMarked<ObjectCreationExpressionSyntax>(tree);
        var argumentList = objectCreation.ArgumentList ?? throw new InvalidOperationException();
        var mutation = mutations.Single();

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(mutation.OperatorId).IsEqualTo(RemoveId);
            _ = await Assert.That(mutation.DisplayName).IsEqualTo("StringComparer.Ordinal => (removed)");
            _ = await Assert.That(mutation.Location.SourceSpan).IsEqualTo(argumentList.Arguments[0].Span);
        }
    }

    /// <summary>
    /// A custom type whose only constructor requires the comparer offers no argument-less overload, so
    /// nothing is removed.
    /// </summary>
    [Test]
    public async Task CreateMutations_NoArgumentLessOverloadExists_ReturnsEmpty()
    {
        var (mutations, _, tree, model, errors) = Mutate(OnlyComparerConstructorSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ResolveConstructor(tree, model)?.Parameters.Length).IsEqualTo(1);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    /// <summary>
    /// The comparer sits behind a required, unrelated argument on a type with only that one constructor.
    /// Dropping the comparer would leave a call the sole overload does not accept.
    /// </summary>
    [Test]
    public async Task CreateMutations_ComparerRequiredToDisambiguate_ReturnsEmpty()
    {
        var (mutations, _, tree, model, errors) = Mutate(RequiresBothArgumentsSource);

        using (Assert.Multiple())
        {
            _ = await Assert.That(errors).IsEqualTo(string.Empty);
            _ = await Assert.That(ResolveConstructor(tree, model)?.Parameters.Length).IsEqualTo(2);
            _ = await Assert.That(mutations.ToArray()).IsEmpty();
        }
    }

    [Test]
    public async Task CreateMutations_NamedComparerArgument_ReturnsEmpty()
    {
        var (mutations, _, _, _, errors) = Mutate(NamedComparerSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NonComparerTrailingArgument_ReturnsEmpty()
    {
        var (mutations, _, _, _, errors) = Mutate(NonComparerArgumentSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    [Test]
    public async Task CreateMutations_NoArguments_ReturnsEmpty()
    {
        var (mutations, _, _, _, errors) = Mutate(NoArgumentsSource);

        _ = await Assert.That(errors).IsEqualTo(string.Empty);
        _ = await Assert.That(mutations.ToArray()).IsEmpty();
    }

    private static string[] OperatorIds(ImmutableArray<Mutation> mutations) =>
        [.. mutations.Select(mutation => mutation.OperatorId)];

    private static IMethodSymbol? ResolveConstructor(SyntaxTree tree, SemanticModel model)
    {
        var objectCreation = SyntaxNodeLocator.FindMarked<ObjectCreationExpressionSyntax>(tree);

        return model.GetSymbolInfo(objectCreation).Symbol as IMethodSymbol;
    }

    private static (
        ImmutableArray<Mutation> Mutations,
        CSharpCompilation Compilation,
        SyntaxTree Tree,
        SemanticModel Model,
        string Errors
    ) Mutate(string source)
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(source);
        var node = SyntaxNodeLocator.FindMarked<ObjectCreationExpressionSyntax>(tree);
        var created = _mutator.CreateMutations(node, semanticModel, CancellationToken.None);
        ImmutableArray<Mutation> mutations = [.. created];
        var errors = Describe(CompilationFactory.GetCompileErrors(compilation));

        return (mutations, compilation, tree, semanticModel, errors);
    }

    private static string Describe(ImmutableArray<Diagnostic> errors) =>
        string.Join(", ", errors.Select(error => error.GetMessage(CultureInfo.InvariantCulture)));
}
