namespace NetEvolve.Frameshift.Tests.Infrastructure;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// An <see cref="AdditionalText" /> that keeps its content in memory, which is how a test hands a
/// test-surface manifest to an analyzer without touching the file system.
/// </summary>
/// <remarks>
/// The default path ends in <c>.frameshift-tests</c>, because that suffix is what both analyzers use to
/// recognise a manifest among the additional files. A test that wants to be ignored on purpose passes a
/// path with a different suffix.
/// </remarks>
internal sealed class InMemoryAdditionalText : AdditionalText
{
    /// <summary>
    /// The path an instance gets when the caller does not choose one.
    /// </summary>
    public const string DefaultPath = "TestSurface.frameshift-tests";

    private readonly SourceText? _text;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAdditionalText" /> class under
    /// <see cref="DefaultPath" />.
    /// </summary>
    /// <param name="text">The content of the file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text" /> is <see langword="null" />.</exception>
    public InMemoryAdditionalText(string text)
        : this(DefaultPath, text) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryAdditionalText" /> class.
    /// </summary>
    /// <param name="path">The path the file reports, which shows up in diagnostic locations.</param>
    /// <param name="text">The content of the file.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path" /> or <paramref name="text" /> is <see langword="null" />.
    /// </exception>
    public InMemoryAdditionalText(string path, string text)
        : this(path, CreateText(text)) { }

    private InMemoryAdditionalText(string path, SourceText? text)
    {
        ArgumentNullException.ThrowIfNull(path);

        Path = path;
        _text = text;
    }

    /// <inheritdoc />
    public override string Path { get; }

    /// <summary>
    /// Creates a file whose content cannot be read, which is the case both analyzers report as an
    /// unreadable manifest.
    /// </summary>
    /// <param name="path">The path the file reports.</param>
    /// <returns>The created file, whose <see cref="GetText" /> returns <see langword="null" />.</returns>
    public static InMemoryAdditionalText WithoutContent(string path = DefaultPath) =>
        new InMemoryAdditionalText(path, (SourceText?)null);

    /// <inheritdoc />
    public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;

    private static SourceText CreateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return SourceText.From(text);
    }
}
