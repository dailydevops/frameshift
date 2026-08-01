namespace NetEvolve.FrameShift.Tests.Unit.Mutations.Operators;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations;
using NetEvolve.FrameShift.Mutations.Operators;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins that <see cref="StringComparerMutator" /> and <see cref="StringComparisonMutator" /> agree on the
/// axis-description text they append to every mutation's display name, for every one of the thirty ordered
/// pairs the six well-known members form.
/// </summary>
/// <remarks>
/// The two operators describe the very same two axes - ordinal versus culture aware, and case sensitive
/// versus case insensitive - over the very same six member names. That shared description logic is meant
/// to live in exactly one place; this test guards the invariant so a future refactor that moves it there
/// cannot accidentally change the wording for one operator while leaving the other one behind.
/// </remarks>
public class StringComparerComparisonAxisParityTests
{
    private const string ComparerTemplate = """
        using System;
        using System.Collections.Generic;

        public class Sample
        {
            public Dictionary<string, int> Create() =>
                new Dictionary<string, int>(StringComparer.MEMBER);
        }
        """;

    private const string ComparisonTemplate = """
        using System;

        public class Sample
        {
            public bool Equal(string left, string right) =>
                string.Equals(left, right, StringComparison.MEMBER);
        }
        """;

    private static readonly string[] _memberNames =
    [
        "CurrentCulture",
        "CurrentCultureIgnoreCase",
        "InvariantCulture",
        "InvariantCultureIgnoreCase",
        "Ordinal",
        "OrdinalIgnoreCase",
    ];

    public static IEnumerable<Func<(string Source, string Target)>> MemberPairs() =>
        _memberNames
            .SelectMany(source =>
                _memberNames
                    .Where(target => !string.Equals(target, source, StringComparison.Ordinal))
                    .Select(target => (source, target))
            )
            .Select(pair => (Func<(string, string)>)(() => pair));

    [Test]
    [MethodDataSource(nameof(MemberPairs))]
    public async Task AxesDescription_ComparerAndComparisonPair_AreWordedIdentically(
        (string Source, string Target) pair
    )
    {
        var comparerAxes = AxesOf(new StringComparerMutator(), ComparerTemplate, pair.Source, pair.Target);
        var comparisonAxes = AxesOf(new StringComparisonMutator(), ComparisonTemplate, pair.Source, pair.Target);

        _ = await Assert.That(comparisonAxes).IsEqualTo(comparerAxes);
    }

    private static string AxesOf(MutationOperatorBase mutator, string template, string source, string target)
    {
        var fixture = template.Replace("MEMBER", source, StringComparison.Ordinal);
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(fixture);
        var node = SyntaxNodeLocator.FindFirst<MemberAccessExpressionSyntax>(
            tree,
            access => _memberNames.Contains(access.Name.Identifier.ValueText, StringComparer.Ordinal)
        );

        var mutations = mutator.CreateMutations(node, semanticModel, CancellationToken.None).ToArray();
        var mutation = mutations.Single(candidate =>
            string.Equals(
                ((MemberAccessExpressionSyntax)candidate.Replacement).Name.Identifier.ValueText,
                target,
                StringComparison.Ordinal
            )
        );

        // The display name is "{Type}.{source} => {Type}.{target} ({axes})"; only the parenthesised axes
        // portion is compared, since the type name and member names are deliberately not shared.
        var displayName = mutation.DisplayName;
        var open = displayName.IndexOf('(', StringComparison.Ordinal);
        var close = displayName.LastIndexOf(')');

        return displayName.Substring(open + 1, close - open - 1);
    }
}
