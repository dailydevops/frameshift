namespace NetEvolve.FrameShift.TestSurface;

/// <summary>
/// The constants defining the on-disk format of a test-surface manifest.
/// </summary>
internal static class TestSurfaceManifestFormat
{
    /// <summary>
    /// The mandatory first non-empty, non-comment line of every manifest.
    /// </summary>
    public const string Header = "frameshift-test-surface/1";

    /// <summary>
    /// The file name suffix identifying a manifest among the additional files.
    /// </summary>
    public const string FileSuffix = ".frameshift-tests";

    /// <summary>
    /// The line prefix marking the documentation comment id of a test method.
    /// </summary>
    public const char TestPrefix = 'T';

    /// <summary>
    /// The line prefix marking the documentation comment id of a referenced production member.
    /// </summary>
    public const char ReferencePrefix = 'R';

    /// <summary>
    /// The line prefix marking a comment line, which is ignored while parsing.
    /// </summary>
    public const char CommentPrefix = '#';
}
