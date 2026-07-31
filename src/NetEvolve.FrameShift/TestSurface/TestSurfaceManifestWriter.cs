namespace NetEvolve.FrameShift.TestSurface;

using System.Text;

/// <summary>
/// Serializes a <see cref="TestSurfaceManifest" /> into its canonical plain text representation,
/// which round-trips through <see cref="TestSurfaceManifestReader" />.
/// </summary>
internal static class TestSurfaceManifestWriter
{
    private const char LineFeed = '\n';

    /// <summary>
    /// Writes the canonical representation of <paramref name="manifest" />: the header line, then one
    /// block per test method, ordered ordinally by the documentation comment id of the test method.
    /// A block is the <c>T</c> line carrying that id and the test case count, followed by the <c>R</c>
    /// lines of the production members the test method references, again ordered ordinally. Lines are
    /// separated by a single line feed and the result ends with a line feed.
    /// </summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>The serialized manifest.</returns>
    /// <remarks>
    /// A referenced production member that belongs to no test method at all cannot be expressed by the
    /// format, because an <c>R</c> line without a preceding <c>T</c> line is malformed. Such a member is
    /// therefore not written. This only happens for a manifest that was built from the flat unions
    /// without a single test method, which no manifest collected from a compilation ever is.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="manifest" /> is <see langword="null" />.</exception>
    public static string Write(TestSurfaceManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var builder = new StringBuilder();

        _ = builder.Append(TestSurfaceManifestFormat.Header).Append(LineFeed);

        foreach (var testMethodId in manifest.TestMethodIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            AppendBlock(builder, manifest, testMethodId);
        }

        return builder.ToString();
    }

    private static void AppendBlock(StringBuilder builder, TestSurfaceManifest manifest, string testMethodId)
    {
        _ = builder
            .Append(TestSurfaceManifestFormat.TestPrefix)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(testMethodId)
            .Append(TestSurfaceManifestFormat.FieldSeparator)
            .Append(manifest.TestCaseCounts[testMethodId].ToString())
            .Append(LineFeed);

        var referencedMemberIds = manifest.ReferencesByTest[testMethodId];

        foreach (var referencedMemberId in referencedMemberIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            _ = builder
                .Append(TestSurfaceManifestFormat.ReferencePrefix)
                .Append(TestSurfaceManifestFormat.FieldSeparator)
                .Append(referencedMemberId)
                .Append(LineFeed);
        }
    }
}
