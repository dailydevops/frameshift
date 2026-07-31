namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

/// <summary>
/// Turns diagnostics into something a test can assert on and a developer can read.
/// </summary>
/// <remarks>
/// An analyzer may run its callbacks concurrently, so the order in which Roslyn hands the diagnostics
/// back is not fixed. Both shapes here therefore order by file, then position, then identifier, which
/// makes an assertion depend on what the analyzer reported instead of on when it happened to report it.
/// Lines and columns are 1-based, like every compiler message.
/// </remarks>
internal static class DiagnosticAssertions
{
    /// <summary>
    /// The text <see cref="Describe(ImmutableArray{Diagnostic})" /> produces for an empty set.
    /// </summary>
    public const string NoDiagnostics = "<no diagnostics>";

    /// <summary>
    /// The file name used for a diagnostic without a file, for example one at <see cref="Location.None" />.
    /// </summary>
    public const string NoFile = "<no file>";

    /// <summary>
    /// Describes every diagnostic on its own line, in the form
    /// <c>FSH0001 Source.cs(3,17): message</c>.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to describe.</param>
    /// <returns>The description, or <see cref="NoDiagnostics" /> if there is none.</returns>
    public static string Describe(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return NoDiagnostics;
        }

        var lines = Order(diagnostics).Select(diagnostic => Describe(diagnostic));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Describes a single diagnostic in the form <c>FSH0001 Source.cs(3,17): message</c>.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to describe.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic" /> is <see langword="null" />.</exception>
    public static string Describe(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var span = diagnostic.Location.GetLineSpan();
        var file = GetFileName(span.Path);
        var line = ToText(span.StartLinePosition.Line + 1);
        var column = ToText(span.StartLinePosition.Character + 1);

        return $"{diagnostic.Id} {file}({line},{column}): {GetMessage(diagnostic)}";
    }

    /// <summary>
    /// Summarises every diagnostic as its identifier, its 1-based line and its message.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to summarise.</param>
    /// <returns>The summaries, possibly empty.</returns>
    public static ImmutableArray<(string Id, int Line, string Message)> Summarise(
        ImmutableArray<Diagnostic> diagnostics
    )
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. Order(diagnostics).Select(diagnostic => Summarise(diagnostic))];
    }

    /// <summary>
    /// Summarises a single diagnostic as its identifier, its 1-based line and its message.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to summarise.</param>
    /// <returns>The summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic" /> is <see langword="null" />.</exception>
    public static (string Id, int Line, string Message) Summarise(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var span = diagnostic.Location.GetLineSpan();

        return (diagnostic.Id, span.StartLinePosition.Line + 1, GetMessage(diagnostic));
    }

    /// <summary>
    /// Collects the identifiers of the diagnostics, in the same order the other shapes use.
    /// </summary>
    /// <param name="diagnostics">The diagnostics to read.</param>
    /// <returns>The identifiers, possibly empty and possibly with repetitions.</returns>
    public static ImmutableArray<string> Ids(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. Order(diagnostics).Select(diagnostic => diagnostic.Id)];
    }

    private static ImmutableArray<Diagnostic> Order(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Sort(CompareByLocation);

    private static int CompareByLocation(Diagnostic left, Diagnostic right)
    {
        var leftSpan = left.Location.GetLineSpan();
        var rightSpan = right.Location.GetLineSpan();
        var result = StringComparer.Ordinal.Compare(leftSpan.Path, rightSpan.Path);

        if (result != 0)
        {
            return result;
        }

        result = leftSpan.StartLinePosition.CompareTo(rightSpan.StartLinePosition);

        return result != 0 ? result : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static string GetMessage(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    private static string GetFileName(string path)
    {
        if (path.Length == 0)
        {
            return NoFile;
        }

        var name = Path.GetFileName(path);

        return name.Length == 0 ? path : name;
    }

    private static string ToText(int value) => value.ToString(CultureInfo.InvariantCulture);
}
