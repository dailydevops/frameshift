namespace NetEvolve.FrameShift.Tests.Unit.Mutations.RegularExpressions;

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Mutations.RegularExpressions;
using NetEvolve.FrameShift.Tests.Unit.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers <see cref="RegexPatternResolution.TryResolve" />, the single place that now locates, validates
/// and tokenizes a candidate pattern literal. It used to be inlined in
/// <c>RegexPatternMutatorBase.CreateMutationsCore</c>; extracting it changed nothing about what is
/// computed, only how often, so these tests pin the very same three-way decision directly instead of only
/// through one operator of the family.
/// </summary>
public class RegexPatternResolutionTests
{
    private const string ArgumentsPlaceholder = "ARGUMENTS";

    private const string CallTemplate = """
        namespace Fixtures;

        using System.Text.RegularExpressions;

        internal static class Patterns
        {
            internal static Regex Create(RegexOptions runtimeOptions) => new Regex(ARGUMENTS);
        }
        """;

    private const string NonPatternSource = """
        namespace Fixtures;

        using System;

        internal static class Patterns
        {
            internal static void Write() => Console.WriteLine(/*!*/"^a$");
        }
        """;

    [Test]
    public async Task TryResolve_ViablePattern_ReturnsSiteOptionsAndTokensThatTileIt()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource("/*!*/\"^a$\""));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(resolution).IsNotNull();
            _ = await Assert.That(resolution!.Site.Pattern).IsEqualTo("^a$");
            _ = await Assert.That(resolution.Options).IsEqualTo(RegexOptions.None);
            _ = await Assert.That(string.Concat(resolution.Tokens.Select(token => token.Text))).IsEqualTo("^a$");
        }
    }

    /// <summary>
    /// <c>RegexOptions.Compiled</c> only decides how the engine is built, never which patterns are legal,
    /// so it is dropped from the options the resolution reports and the tokens are produced under the
    /// options with the flag removed.
    /// </summary>
    [Test]
    public async Task TryResolve_CompiledOption_IsDroppedFromTheReportedOptions()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(
            CreateCallSource("/*!*/\"^a$\", RegexOptions.Compiled")
        );
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(resolution).IsNotNull();
        _ = await Assert.That(resolution!.Options.HasFlag(RegexOptions.Compiled)).IsFalse();
    }

    [Test]
    public async Task TryResolve_LiteralThatIsNoPatternSite_ReturnsNull()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(NonPatternSource);
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(resolution).IsNull();
    }

    [Test]
    public async Task TryResolve_OptionsNotStaticallyDeterminable_ReturnsNull()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(
            CreateCallSource("/*!*/\"^a$\", runtimeOptions")
        );
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(resolution).IsNull();
    }

    /// <summary>
    /// A reversed character class range tokenizes - the lexer never decides whether a range is sensible -
    /// but the <see cref="Regex" /> constructor rejects it, so the resolution has to fail on the validity
    /// check even though the tokenizer alone would have succeeded.
    /// </summary>
    [Test]
    public async Task TryResolve_PatternThatOnlyFailsValidity_ReturnsNull()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource("/*!*/\"[z-a]\""));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(resolution).IsNull();
    }

    /// <summary>
    /// An unterminated character class fails the tokenizer itself, which is checked after validity and
    /// therefore never reached for this pattern - proving the third and last check is exercised too.
    /// </summary>
    [Test]
    public async Task TryResolve_PatternThatFailsTokenization_ReturnsNull()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource("/*!*/\"[a-\""));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);

        var resolution = RegexPatternResolution.TryResolve(node, semanticModel, CancellationToken.None);

        _ = await Assert.That(resolution).IsNull();
    }

    [Test]
    public async Task TryResolve_CancellationRequested_ThrowsOperationCanceledException()
    {
        var (_, semanticModel, tree) = CompilationFactory.CreateWithModel(CreateCallSource("/*!*/\"^a$\""));
        var node = SyntaxNodeLocator.FindMarked<LiteralExpressionSyntax>(tree);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            RegexPatternResolution.TryResolve(node, semanticModel, cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static string CreateCallSource(string arguments) =>
        CallTemplate.Replace(ArgumentsPlaceholder, arguments, StringComparison.Ordinal);
}
