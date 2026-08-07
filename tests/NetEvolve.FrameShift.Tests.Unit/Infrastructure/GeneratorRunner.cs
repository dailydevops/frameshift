namespace NetEvolve.FrameShift.Tests.Unit.Infrastructure;

using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Runs one <see cref="IIncrementalGenerator" /> over a compilation and returns what it produced.
/// </summary>
/// <remarks>
/// <para>
/// This is the generator counterpart of <see cref="AnalyzerRunner" />, and it fails for the same reason:
/// a generator that throws does not fail the build of the driver, it merely turns into a
/// <c>CS8785</c> diagnostic and an <see cref="GeneratorRunResult.Exception" /> on the run result. A test
/// that only looked at the generated sources would then happily assert "nothing was generated" while the
/// generator under test actually crashed, therefore every run rethrows loudly.
/// </para>
/// <para>
/// The MSBuild properties of FrameShift reach a generator through the global analyzer configuration, so
/// the runner installs them with <see cref="GeneratorDriver.WithUpdatedAnalyzerConfigOptions" /> under
/// the very <c>build_property.*</c> keys the compiler generates for a <c>CompilerVisibleProperty</c>.
/// </para>
/// </remarks>
internal static class GeneratorRunner
{
    /// <summary>
    /// The identifier the compiler reports a generator exception under.
    /// </summary>
    public const string GeneratorFailureId = "CS8785";

    /// <summary>
    /// Runs <paramref name="generator" /> over <paramref name="compilation" />.
    /// </summary>
    /// <param name="generator">The generator under test.</param>
    /// <param name="compilation">The compilation the generator sees.</param>
    /// <param name="globalOptions">
    /// The global analyzer configuration, keyed by the <c>build_property.*</c> names the compiler
    /// generates for a <c>CompilerVisibleProperty</c>.
    /// </param>
    /// <param name="cancellationToken">A token to observe while generating.</param>
    /// <returns>The generated sources, the driver diagnostics and the compilation including the output.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="generator" /> or <paramref name="compilation" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="InvalidOperationException">The generator threw an exception.</exception>
    public static Output Run(
        IIncrementalGenerator generator,
        Compilation compilation,
        IReadOnlyDictionary<string, string>? globalOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(compilation);

        var options = new TestAnalyzerConfigOptions(globalOptions).AsProvider();

        var driver = CSharpGeneratorDriver
            .Create(generator.AsSourceGenerator())
            .WithUpdatedParseOptions(CompilationFactory.ParseOptions)
            .WithUpdatedAnalyzerConfigOptions(options)
            .RunGeneratorsAndUpdateCompilation(
                compilation,
                out var outputCompilation,
                out var diagnostics,
                cancellationToken
            );

        var runResult = driver.GetRunResult();

        ThrowOnGeneratorFailure(runResult);

        return new Output(CollectSources(runResult), diagnostics, outputCompilation);
    }

    /// <summary>
    /// Flattens the generated sources of every generator in the run into hint name and text pairs.
    /// </summary>
    /// <param name="runResult">The result of the driver run.</param>
    /// <returns>The generated sources, in the order the driver reported them.</returns>
    private static ImmutableArray<(string HintName, SourceText Text)> CollectSources(
        GeneratorDriverRunResult runResult
    ) =>
        [
            .. runResult
                .Results.SelectMany(result => result.GeneratedSources)
                .Select(source => (source.HintName, source.SourceText)),
        ];

    /// <summary>
    /// Fails the current test when the generator crashed instead of generating.
    /// </summary>
    /// <param name="runResult">The result of the driver run.</param>
    /// <exception cref="InvalidOperationException">At least one generator threw.</exception>
    private static void ThrowOnGeneratorFailure(GeneratorDriverRunResult runResult)
    {
        var failures = runResult.Results.Where(result => result.Exception is not null).ToImmutableArray();

        if (failures.IsEmpty)
        {
            return;
        }

        var messages = failures.Select(failure =>
            failure.Generator.GetGeneratorType().Name + ": " + failure.Exception!.ToString()
        );

        throw new InvalidOperationException(
            "The generator threw an exception, which the driver reported as "
                + GeneratorFailureId
                + ":"
                + Environment.NewLine
                + string.Join(Environment.NewLine, messages)
        );
    }

    /// <summary>
    /// Everything one generator run produced.
    /// </summary>
    internal sealed class Output
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Output" /> class.
        /// </summary>
        /// <param name="sources">The generated sources, keyed by their hint name.</param>
        /// <param name="diagnostics">The diagnostics the driver reported.</param>
        /// <param name="compilation">The input compilation with every generated tree added.</param>
        public Output(
            ImmutableArray<(string HintName, SourceText Text)> sources,
            ImmutableArray<Diagnostic> diagnostics,
            Compilation compilation
        )
        {
            ArgumentNullException.ThrowIfNull(compilation);

            Sources = sources.IsDefault ? [] : sources;
            Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
            Compilation = compilation;
        }

        /// <summary>
        /// Gets the generated sources, in the order the driver reported them.
        /// </summary>
        public ImmutableArray<(string HintName, SourceText Text)> Sources { get; }

        /// <summary>
        /// Gets the diagnostics the driver reported, which are the ones the generator itself added.
        /// </summary>
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        /// <summary>
        /// Gets the input compilation with every generated tree added, so that a test can prove the
        /// generated code actually compiles.
        /// </summary>
        public Compilation Compilation { get; }

        /// <summary>
        /// Gets the hint names of the generated sources, joined by <c>|</c>, which makes a failing
        /// expectation readable.
        /// </summary>
        public string HintNames => string.Join("|", Sources.Select(source => source.HintName));

        /// <summary>
        /// Gets the text of the single source generated under <paramref name="hintName" />.
        /// </summary>
        /// <param name="hintName">The hint name to look for.</param>
        /// <returns>The generated text.</returns>
        /// <exception cref="InvalidOperationException">
        /// There is not exactly one generated source with that hint name.
        /// </exception>
        public string TextOf(string hintName)
        {
            var matches = Sources
                .Where(source => string.Equals(source.HintName, hintName, StringComparison.Ordinal))
                .ToImmutableArray();

            if (matches.Length != 1)
            {
                // Concatenation with an explicitly invariant count keeps the message identical on every
                // target framework: .NET Framework has neither the interpolated-string handler overload of
                // string.Create nor a culture-invariant default for an interpolated int.
                throw new InvalidOperationException(
                    "Expected exactly one generated source '"
                        + hintName
                        + "', but found "
                        + matches.Length.ToString(CultureInfo.InvariantCulture)
                        + " of them; generated were: "
                        + HintNames
                        + "."
                );
            }

            return matches[0].Text.ToString();
        }
    }
}
