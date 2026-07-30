namespace NetEvolve.Frameshift.TestSurface;

using System.Text;

/// <summary>
/// Serializes a <see cref="TestSurfaceManifest" /> into its canonical plain text representation,
/// which round-trips through <see cref="TestSurfaceManifestReader" />.
/// </summary>
internal static class TestSurfaceManifestWriter
{
    private const char LineFeed = '\n';

    /// <summary>
    /// Writes the canonical representation of <paramref name="manifest" />: the header line, then
    /// the test method entries, then the referenced member entries. Both groups are sorted
    /// ordinally, lines are separated by a single line feed and the result ends with a line feed.
    /// </summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>The serialized manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest" /> is <see langword="null" />.</exception>
    public static string Write(TestSurfaceManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        var builder = new StringBuilder();

        _ = builder.Append(TestSurfaceManifestFormat.Header).Append(LineFeed);

        AppendEntries(builder, TestSurfaceManifestFormat.TestPrefix, manifest.TestMethodIds);
        AppendEntries(builder, TestSurfaceManifestFormat.ReferencePrefix, manifest.ReferencedMemberIds);

        return builder.ToString();
    }

    private static void AppendEntries(StringBuilder builder, char prefix, IEnumerable<string> ids)
    {
        foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal))
        {
            _ = builder.Append(prefix).Append(' ').Append(id).Append(LineFeed);
        }
    }
}
