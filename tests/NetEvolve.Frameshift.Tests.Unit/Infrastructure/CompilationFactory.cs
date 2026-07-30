namespace NetEvolve.Frameshift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Creates the in-memory C# compilations the analyzer and the mutation engine are exercised against.
/// </summary>
/// <remarks>
/// Every compilation uses the latest language version, is a library and has nullable reference types
/// enabled, so that a fixture behaves exactly like the production code this repository analyses.
/// </remarks>
internal static class CompilationFactory
{
    /// <summary>
    /// The assembly name a compilation gets when the caller does not choose one.
    /// </summary>
    public const string DefaultAssemblyName = "TestAssembly";

    /// <summary>
    /// The file path a single-file compilation gets when the caller does not choose one.
    /// </summary>
    public const string DefaultFilePath = "Source.cs";

    private static readonly CSharpParseOptions _parseOptions = new CSharpParseOptions(LanguageVersion.Latest);

    private static readonly CSharpCompilationOptions _compilationOptions = new CSharpCompilationOptions(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable
    );

    /// <summary>
    /// Gets the parse options every syntax tree of a test compilation is parsed with.
    /// </summary>
    public static CSharpParseOptions ParseOptions => _parseOptions;

    /// <summary>
    /// Gets the compilation options every test compilation is created with.
    /// </summary>
    public static CSharpCompilationOptions CompilationOptions => _compilationOptions;

    /// <summary>
    /// Parses a single source file exactly the way the <c>Create</c> overloads do.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <param name="filePath">The path the tree reports, which shows up in diagnostic locations.</param>
    /// <returns>The parsed tree.</returns>
    public static SyntaxTree ParseTree(string source, string filePath = DefaultFilePath) =>
        CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), _parseOptions, filePath);

    /// <summary>
    /// Creates a compilation from a single source file.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <param name="assemblyName">The assembly name of the compilation.</param>
    /// <param name="includeTUnit">Whether the TUnit assemblies are referenced, which turns the compilation into a test assembly.</param>
    /// <param name="additionalReferences">References added on top of the default ones.</param>
    /// <param name="filePath">The path the single syntax tree reports.</param>
    /// <returns>The created compilation.</returns>
    public static CSharpCompilation Create(
        string source,
        string assemblyName = DefaultAssemblyName,
        bool includeTUnit = false,
        IEnumerable<MetadataReference>? additionalReferences = null,
        string filePath = DefaultFilePath
    ) => Create([(filePath, source)], assemblyName, includeTUnit, additionalReferences);

    /// <summary>
    /// Creates a compilation from several source files, which is what the reachability tests need.
    /// </summary>
    /// <param name="sources">The files of the compilation, each one a path and its source text.</param>
    /// <param name="assemblyName">The assembly name of the compilation.</param>
    /// <param name="includeTUnit">Whether the TUnit assemblies are referenced, which turns the compilation into a test assembly.</param>
    /// <param name="additionalReferences">References added on top of the default ones.</param>
    /// <returns>The created compilation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sources" /> is <see langword="null" />.</exception>
    public static CSharpCompilation Create(
        IEnumerable<(string FilePath, string Source)> sources,
        string assemblyName = DefaultAssemblyName,
        bool includeTUnit = false,
        IEnumerable<MetadataReference>? additionalReferences = null
    )
    {
        ArgumentNullException.ThrowIfNull(sources);

        var trees = sources.Select(source => ParseTree(source.Source, source.FilePath));
        var references = ReferenceAssemblies.For(includeTUnit);

        if (additionalReferences is not null)
        {
            references = references.AddRange(additionalReferences);
        }

        return CSharpCompilation.Create(assemblyName, trees, references, _compilationOptions);
    }

    /// <summary>
    /// Creates a single-file compilation and hands back its semantic model and syntax tree, which is
    /// what the mutation operators are tested with.
    /// </summary>
    /// <param name="source">The C# source text.</param>
    /// <param name="assemblyName">The assembly name of the compilation.</param>
    /// <param name="includeTUnit">Whether the TUnit assemblies are referenced.</param>
    /// <param name="additionalReferences">References added on top of the default ones.</param>
    /// <param name="filePath">The path the single syntax tree reports.</param>
    /// <returns>The compilation, the semantic model of its only tree, and that tree.</returns>
    public static (CSharpCompilation Compilation, SemanticModel SemanticModel, SyntaxTree Tree) CreateWithModel(
        string source,
        string assemblyName = DefaultAssemblyName,
        bool includeTUnit = false,
        IEnumerable<MetadataReference>? additionalReferences = null,
        string filePath = DefaultFilePath
    )
    {
        var compilation = Create(source, assemblyName, includeTUnit, additionalReferences, filePath);
        var tree = compilation.SyntaxTrees[0];

        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    /// <summary>
    /// Collects the errors of <paramref name="compilation" />, so that a test can prove its own fixture
    /// compiles. A fixture that does not compile makes the test around it meaningless.
    /// </summary>
    /// <param name="compilation">The compilation to check.</param>
    /// <param name="cancellationToken">A token to observe while binding.</param>
    /// <returns>The errors, possibly empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation" /> is <see langword="null" />.</exception>
    public static ImmutableArray<Diagnostic> GetCompileErrors(
        Compilation compilation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return
        [
            .. compilation
                .GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
        ];
    }
}
