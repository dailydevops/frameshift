namespace NetEvolve.FrameShift.TestSurface;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Parses the plain text representation of a test-surface manifest, as produced by
/// <see cref="TestSurfaceManifestWriter" />.
/// </summary>
/// <remarks>
/// The manifest is a sequence of blocks: a <c>T</c> line names a test method and its test case count,
/// and every following <c>R</c> line names a production member that this very test method references.
/// A line carrying an unknown marker is ignored, so that a future manifest version stays readable.
/// </remarks>
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

        var blocks = new BlockBuilder();
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

            if (!TryReadEntry(content, lineNumber, blocks, out error))
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

        manifest = blocks.Build();

        return true;
    }

    /// <summary>
    /// Validates the first non-empty, non-comment line against the expected manifest header.
    /// </summary>
    /// <param name="content">The trimmed content of the line.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns>
    /// <see langword="true" /> if the line is the expected header; otherwise <see langword="false" />.
    /// </returns>
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
    /// Reads a single entry line and adds it to <paramref name="blocks" />. Lines with an unknown
    /// marker are ignored, so that future manifest versions stay readable.
    /// </summary>
    /// <param name="content">The trimmed content of the line.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="blocks">The builder collecting the per-test blocks.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if the line is well-formed; otherwise <see langword="false" />.</returns>
    private static bool TryReadEntry(string content, int lineNumber, BlockBuilder blocks, out string? error)
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

        var arguments = separatorIndex < 0 ? string.Empty : content.Substring(separatorIndex + 1).Trim();

        if (arguments.Length == 0)
        {
            error = $"Line {lineNumber}: the '{marker[0]}' entry does not specify a documentation " + "comment id.";

            return false;
        }

        return isTestLine
            ? TryReadTest(arguments, lineNumber, blocks, out error)
            : TryReadReference(arguments, lineNumber, blocks, out error);
    }

    /// <summary>
    /// Reads a <c>T</c> line, which opens the block of a test method and carries its test case count.
    /// </summary>
    /// <param name="arguments">The trimmed part of the line following the marker.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="blocks">The builder collecting the per-test blocks.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if the line is well-formed; otherwise <see langword="false" />.</returns>
    private static bool TryReadTest(string arguments, int lineNumber, BlockBuilder blocks, out string? error)
    {
        var separatorIndex = IndexOfWhiteSpace(arguments);

        if (separatorIndex < 0)
        {
            error =
                $"Line {lineNumber}: the '{TestSurfaceManifestFormat.TestPrefix}' entry for "
                + $"'{arguments}' does not specify a test case count.";

            return false;
        }

        var testMethodId = arguments.Substring(0, separatorIndex);
        var countText = arguments.Substring(separatorIndex + 1).Trim();

        if (!TestCaseCount.TryParse(countText, out var count))
        {
            error = $"Line {lineNumber}: '{countText}' is not a valid test case count.";

            return false;
        }

        if (!blocks.TryOpen(testMethodId, count))
        {
            error =
                $"Line {lineNumber}: the '{TestSurfaceManifestFormat.TestPrefix}' entry for "
                + $"'{testMethodId}' is declared more than once.";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>
    /// Reads an <c>R</c> line, which adds a referenced production member to the enclosing block.
    /// </summary>
    /// <param name="arguments">The trimmed part of the line following the marker.</param>
    /// <param name="lineNumber">The 1-based number of the line.</param>
    /// <param name="blocks">The builder collecting the per-test blocks.</param>
    /// <param name="error">The description of the problem, or <see langword="null" /> on success.</param>
    /// <returns><see langword="true" /> if the line is well-formed; otherwise <see langword="false" />.</returns>
    private static bool TryReadReference(string arguments, int lineNumber, BlockBuilder blocks, out string? error)
    {
        if (!blocks.TryAddReference(arguments))
        {
            error =
                $"Line {lineNumber}: the '{TestSurfaceManifestFormat.ReferencePrefix}' entry appears "
                + $"before any '{TestSurfaceManifestFormat.TestPrefix}' entry.";

            return false;
        }

        error = null;

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

    /// <summary>
    /// Collects the per-test blocks of a manifest while it is parsed. The instance lives for the
    /// duration of a single <see cref="TryRead" /> call and is therefore not shared between threads.
    /// </summary>
    private sealed class BlockBuilder
    {
        private readonly ImmutableDictionary<string, TestCaseCount>.Builder _counts;
        private readonly Dictionary<string, ImmutableHashSet<string>.Builder> _references;
        private ImmutableHashSet<string>.Builder? _current;

        /// <summary>
        /// Initializes a new instance of the <see cref="BlockBuilder" /> class.
        /// </summary>
        public BlockBuilder()
        {
            _counts = ImmutableDictionary.CreateBuilder<string, TestCaseCount>(StringComparer.Ordinal);
            _references = new Dictionary<string, ImmutableHashSet<string>.Builder>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Opens the block of the test method <paramref name="testMethodId" />.
        /// </summary>
        /// <param name="testMethodId">The documentation comment id of the test method.</param>
        /// <param name="count">The test case count of the test method.</param>
        /// <returns>
        /// <see langword="true" /> if the block was opened; <see langword="false" /> if the same test
        /// method was already declared, which is malformed.
        /// </returns>
        public bool TryOpen(string testMethodId, TestCaseCount count)
        {
            if (_counts.ContainsKey(testMethodId))
            {
                return false;
            }

            _counts[testMethodId] = count;
            _current = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            _references[testMethodId] = _current;

            return true;
        }

        /// <summary>
        /// Adds a referenced production member to the currently open block.
        /// </summary>
        /// <param name="referencedMemberId">The documentation comment id of the production member.</param>
        /// <returns>
        /// <see langword="true" /> if the member was added; <see langword="false" /> if no block is open
        /// yet, which is malformed.
        /// </returns>
        public bool TryAddReference(string referencedMemberId)
        {
            if (_current is null)
            {
                return false;
            }

            _ = _current.Add(referencedMemberId);

            return true;
        }

        /// <summary>
        /// Builds the parsed manifest from the collected blocks.
        /// </summary>
        /// <returns>The parsed manifest.</returns>
        public TestSurfaceManifest Build()
        {
            var references = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(
                StringComparer.Ordinal
            );

            foreach (var entry in _references)
            {
                references[entry.Key] = entry.Value.ToImmutable();
            }

            return new TestSurfaceManifest(_counts.ToImmutable(), references.ToImmutable());
        }
    }
}
