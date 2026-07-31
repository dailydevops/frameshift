namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// An <see cref="AnalyzerConfigOptions" /> backed by a plain dictionary, which is how a test hands the
/// MSBuild configuration of FrameShift to an analyzer.
/// </summary>
/// <remarks>
/// The keys are the ones the compiler generates for a <c>CompilerVisibleProperty</c>, meaning the
/// MSBuild property name prefixed with <c>build_property.</c>, and they are compared with
/// <see cref="AnalyzerConfigOptions.KeyComparer" /> so that a test sees the same case-insensitive
/// lookup the real build has.
/// </remarks>
internal sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
{
    private readonly ImmutableDictionary<string, string> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestAnalyzerConfigOptions" /> class.
    /// </summary>
    /// <param name="options">The options to expose, or <see langword="null" /> for none.</param>
    public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string>? options) =>
        _options = options is null
            ? ImmutableDictionary<string, string>.Empty.WithComparers(KeyComparer)
            : ImmutableDictionary.CreateRange(KeyComparer, options);

    /// <summary>
    /// Gets an instance without a single option, which is what an unconfigured project looks like.
    /// </summary>
    public static TestAnalyzerConfigOptions Empty { get; } = new TestAnalyzerConfigOptions(options: null);

    /// <inheritdoc />
    public override IEnumerable<string> Keys => _options.Keys;

    /// <inheritdoc />
    public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) =>
        _options.TryGetValue(key, out value);

    /// <summary>
    /// Wraps these options in a provider that serves them as the global options and as the options of
    /// every syntax tree and additional file.
    /// </summary>
    /// <returns>The created provider.</returns>
    public AnalyzerConfigOptionsProvider AsProvider() => new TestProvider(this);

    /// <summary>
    /// The minimal <see cref="AnalyzerConfigOptionsProvider" /> serving one set of options everywhere.
    /// </summary>
    internal sealed class TestProvider : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="TestProvider" /> class.
        /// </summary>
        /// <param name="options">The options served by this provider.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options" /> is <see langword="null" />.</exception>
        public TestProvider(AnalyzerConfigOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            _options = options;
        }

        /// <inheritdoc />
        public override AnalyzerConfigOptions GlobalOptions => _options;

        /// <inheritdoc />
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _options;

        /// <inheritdoc />
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _options;
    }
}
