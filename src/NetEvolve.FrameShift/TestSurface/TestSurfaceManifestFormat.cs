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
    /// The line prefix opening the block of a test method. The line carries the documentation comment
    /// id of the test method, followed by its test case count.
    /// </summary>
    public const char TestPrefix = 'T';

    /// <summary>
    /// The line prefix marking the documentation comment id of a production member referenced by the
    /// test method of the enclosing block. Such a line belongs to the preceding
    /// <see cref="TestPrefix" /> line and is malformed before the first one.
    /// </summary>
    public const char ReferencePrefix = 'R';

    /// <summary>
    /// The line prefix marking the documentation comment id of a production member that the test method
    /// of the enclosing block references with a credible basis for believing a mutation of it would be
    /// observed: the reference is an actual invocation (not a bare method-group conversion) and the test
    /// method, or a member it reaches, also calls a recognised, non-trivial assertion API. Such a line
    /// belongs to the preceding <see cref="TestPrefix" /> line, exactly like <see cref="ReferencePrefix" />,
    /// and is a subset of that block's references.
    /// </summary>
    /// <remarks>
    /// An older reader that does not know this marker ignores the line, exactly as it ignores any other
    /// unknown marker, so a manifest carrying it stays readable by an older analyzer version - it simply
    /// falls back to reachability-only precision for every mutation point.
    /// </remarks>
    public const char BehavioralReferencePrefix = 'B';

    /// <summary>
    /// The line prefix marking a comment line, which is ignored while parsing.
    /// </summary>
    public const char CommentPrefix = '#';

    /// <summary>
    /// The suffix marking a test case count as a lower bound instead of an exact number, as in
    /// <c>1+</c>.
    /// </summary>
    public const char LowerBoundSuffix = '+';

    /// <summary>
    /// The character separating the fields of an entry line.
    /// </summary>
    public const char FieldSeparator = ' ';
}
