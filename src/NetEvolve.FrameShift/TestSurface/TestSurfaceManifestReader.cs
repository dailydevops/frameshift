namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Parses the plain text representation of a test-surface manifest, as produced by
/// <see cref="TestSurfaceManifestWriter" />.
/// </summary>
internal static class TestSurfaceManifestReader
{
    /// <summary>
    /// Tries to parse the content of a test-surface manifest.
    /// </summary>
    /// <param name="text">The content of the manifest, read line by line.</param>
    /// <param name="manifest">
    /// When this method returns <see langword="true" />, the parsed manifest; otherwise
    /// <see cref="TestSurfaceManifest.Empty" />.
    /// </param>
    /// <param name="error">
    /// When this method returns <see langword="false" />, a description of the problem including the
    /// affected 1-based line number; otherwise <see langword="null" />.
    /// </param>
    /// <returns>
    /// <see langword="true" /> if <paramref name="text" /> is a well-formed manifest; otherwise
    /// <see langword="false" />.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is <see langword="null" />.</exception>
    public static bool TryRead(SourceText text, out TestSurfaceManifest manifest, out string? error)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        manifest = TestSurfaceManifest.Empty;
        error = null;

        var testMethodIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var referencedMemberIds = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var headerFound = false;

        foreach (var line in text.Lines)
        {
            var lineNumber = line.LineNumber + 1;
            var content = line.ToString().Trim();

            if (content.Length == 0 || content[0] == TestSurfaceManifestFormat.CommentPrefix)
            {
                continue;
            }

            if (!headerFound)
            {
                if (!TryReadHeader(content, lineNumber, out error))
                {
                    return false;
                }

                headerFound = true;
                continue;
            }

            if (!TryReadEntry(content, lineNumber, testMethodIds, referencedMemberIds, out error))
            {
                return false;
            }
        }

        if (!headerFound)
        {
            error =
                "The test-surface manifest does not contain the required header "
                + $"'{TestSurfaceManifestFormat.Header}'.";

            return false;
        }

        manifest = new TestSurfaceManifest(testMethodIds.ToImmutable(), referencedMemberIds.ToImmutable());

        return true;
    }

    /// <summary>
    /// Validates the first non-empty, non-comment line against the expected manifest header.
    /// </summary>
    /// <param name="content">The trimmed content of the line.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if the line is the expected header; otherwise <see langword="false" />.</returns>
    private static bool TryReadHeader(string content, int lineNumber, out string? error)
    {
        if (string.Equals(content, TestSurfaceManifestFormat.Header, StringComparison.Ordinal))
        {
            error = null;

            return true;
        }

        error =
            $"Line {lineNumber}: expected the test-surface manifest header "
            + $"'{TestSurfaceManifestFormat.Header}', but found '{content}'.";

        return false;
    }

    /// <summary>
    /// Reads a single entry line and adds its documentation comment id to the matching builder.
    /// Lines with an unknown marker are ignored, so that future manifest versions stay readable.
    /// </summary>
    /// <param name="content">The trimmed content of the line.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="testMethodIds">The builder collecting the test method ids.</param>
    /// <param name="referencedMemberIds">The builder collecting the referenced production member ids.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if the line is well-formed; otherwise <see langword="false" />.</returns>
    private static bool TryReadEntry(
        string content,
        int lineNumber,
        ImmutableHashSet<string>.Builder testMethodIds,
        ImmutableHashSet<string>.Builder referencedMemberIds,
        out string? error
    )
    {
        error = null;

        var separatorIndex = IndexOfWhiteSpace(content);
        var marker = separatorIndex < 0 ? content : content.Substring(0, separatorIndex);

        if (marker.Length != 1)
        {
            return true;
        }

        var isTestLine = marker[0] == TestSurfaceManifestFormat.TestPrefix;
        var isReferenceLine = marker[0] == TestSurfaceManifestFormat.ReferencePrefix;

        if (!isTestLine && !isReferenceLine)
        {
            return true;
        }

        var id = separatorIndex < 0 ? string.Empty : content.Substring(separatorIndex + 1).Trim();

        if (id.Length == 0)
        {
            error = $"Line {lineNumber}: the '{marker[0]}' entry does not specify a documentation " + "comment id.";

            return false;
        }

        if (isTestLine)
        {
            _ = testMethodIds.Add(id);
        }
        else
        {
            _ = referencedMemberIds.Add(id);
        }

        return true;
    }

    private static int IndexOfWhiteSpace(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
