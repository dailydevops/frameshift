namespace NetEvolve.FrameShift.Tests.Infrastructure;

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using MSTestFramework = Microsoft.VisualStudio.TestTools.UnitTesting;
#if NETFRAMEWORK
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
#endif

/// <summary>
/// The metadata references every test compilation is built against.
/// </summary>
/// <remarks>
/// <para>
/// On .NET the references are taken from the assemblies the current process already trusts, which the
/// host publishes as <c>TRUSTED_PLATFORM_ASSEMBLIES</c>. That list contains the runtime of the executing
/// framework <em>and</em> every package assembly of the test project, so it is narrowed down to the
/// directory the runtime itself lives in. Without that filter every test compilation would silently
/// reference TUnit and the analyzer assembly, and the test-side analyzer classifies a compilation as a
/// test assembly by exactly that reference; tests asserting that it stays silent could then never fail.
/// </para>
/// <para>
/// On .NET Framework that key does not exist, because it is a CoreCLR concept. The runtime assemblies are
/// therefore looked up by name in the runtime directory, from a list derived from what the fixtures of
/// this suite actually compile, and a compiler-support type .NET Framework never shipped is compiled into
/// an in-memory assembly and added to every set.
/// </para>
/// <para>
/// A test framework is therefore added back explicitly, and always from the real package assemblies:
/// the framework adapters are recognised by metadata names and base types, so a hand-written look-alike
/// would hide exactly the defects these tests exist to catch. Every set is seeded with a type that only
/// its own framework declares, and the assembly reference graph is walked from there.
/// </para>
/// <para>
/// Building a <see cref="MetadataReference" /> reads and maps a file, therefore every set is computed
/// once per process and shared by every compilation.
/// </para>
/// </remarks>
internal static class ReferenceAssemblies
{
    private const string AssemblyExtension = ".dll";

#if !NETFRAMEWORK
    private const string TrustedPlatformAssembliesKey = "TRUSTED_PLATFORM_ASSEMBLIES";
#endif

#if NETFRAMEWORK
    /// <summary>
    /// The name of the assembly holding the compiler-support types of <see cref="CompilerSupport" />.
    /// </summary>
    private const string CompilerSupportAssemblyName = "FrameShift.CompilerSupport";

    /// <summary>
    /// The compiler-support type .NET Framework never shipped, declared with the same name, namespace and
    /// accessibility the runtime uses on .NET. <c>IsExternalInit</c> is what an <c>init</c> accessor - and
    /// therefore every positional record - is compiled against; without it a fixture declaring a record
    /// fails with CS0518, and the tests asserting that their own fixture compiles would fail for a reason
    /// that has nothing to do with the analyzers. The declaration carries no behaviour at all: the compiler
    /// only needs the type to exist.
    /// </summary>
    private const string CompilerSupportSource = """
        namespace System.Runtime.CompilerServices
        {
            public static class IsExternalInit
            {
            }
        }
        """;

    /// <summary>
    /// The .NET Framework runtime assemblies without which no fixture binds at all:
    /// <list type="bullet">
    /// <item><c>mscorlib</c> - <c>object</c> and the primitives, <c>Attribute</c> with
    /// <c>AttributeUsage</c> and <c>AttributeTargets</c>, <c>Action</c>, <c>Func</c>, <c>EventHandler</c>,
    /// <c>EventArgs</c>, <c>Obsolete</c>, <c>Console</c>, <c>Math</c>, <c>Convert</c>, <c>DateTime</c>,
    /// <c>TimeSpan</c>, <c>IntPtr</c>, the exceptions the fixtures throw, <c>IEnumerable&lt;T&gt;</c> and
    /// the other generic collections, <c>System.Threading.Tasks.Task</c> and
    /// <c>System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute</c>.</item>
    /// <item><c>System</c> - <c>System.CodeDom.Compiler.GeneratedCodeAttribute</c>, which the generated-code
    /// fixtures carry, and the assembly every net462 and net472 test-framework package references.</item>
    /// <item><c>System.Core</c> - <c>System.Linq</c>, used by the fixtures that call <c>Select</c> and
    /// <c>Sum</c>, and <c>ExtensionAttribute</c>, without which no extension method can be declared.</item>
    /// </list>
    /// </summary>
    private static readonly ImmutableArray<string> _requiredFrameworkAssemblies = ["mscorlib", "System", "System.Core"];

    /// <summary>
    /// The .NET Framework runtime assemblies that are added when the runtime directory has them:
    /// <list type="bullet">
    /// <item><c>System.Runtime</c> and <c>netstandard</c> - the type-forwarding facades a netstandard2.0
    /// assembly is compiled against. TUnit reaches .NET Framework through netstandard2.0, and without the
    /// facades none of its types can be bound by a fixture.</item>
    /// <item><c>Microsoft.CSharp</c> - the runtime binder. A fixture using <c>dynamic</c> is reported as
    /// CS0656 <em>missing compiler required member</em> without it.</item>
    /// <item><c>System.Numerics</c> - so that the namespace of the generic-math fixtures resolves; note
    /// that <c>INumber&lt;T&gt;</c> itself does not exist on .NET Framework at all.</item>
    /// <item><c>System.ValueTuple</c> - the facade forwarding to the tuple types in <c>mscorlib</c>, which
    /// netstandard2.0 assemblies reference.</item>
    /// </list>
    /// </summary>
    private static readonly ImmutableArray<string> _optionalFrameworkAssemblies =
    [
        "System.Runtime",
        "netstandard",
        "Microsoft.CSharp",
        "System.Numerics",
        "System.ValueTuple",
    ];

    private static readonly Lazy<ImmutableArray<MetadataReference>> _compilerSupport = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateCompilerSupport, LazyThreadSafetyMode.ExecutionAndPublication);
#endif

    private static readonly Lazy<ImmutableArray<string>> _frameworkPaths = new Lazy<ImmutableArray<string>>(
        GetFrameworkPaths,
        LazyThreadSafetyMode.ExecutionAndPublication
    );

    private static readonly Lazy<ImmutableArray<MetadataReference>> _default = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateDefault, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withTUnit = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithTUnit, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withXunitV2 = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithXunitV2, LazyThreadSafetyMode.ExecutionAndPublication);

#if FRAMESHIFT_XUNIT_V3
    private static readonly Lazy<ImmutableArray<MetadataReference>> _withXunitV3 = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithXunitV3, LazyThreadSafetyMode.ExecutionAndPublication);
#endif

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withNUnit = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithNUnit, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withMSTest = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithMSTest, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<ImmutableArray<MetadataReference>> _withAllFrameworks = new Lazy<
        ImmutableArray<MetadataReference>
    >(CreateWithAllFrameworks, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the runtime references of the currently executing framework, deduplicated by path.
    /// </summary>
    public static ImmutableArray<MetadataReference> Default => _default.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the assembly declaring <c>TUnit.Core.TestAttribute</c> and every
    /// assembly it transitively needs, so that a fixture can carry real <c>[Test]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithTUnit => _withTUnit.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the xUnit.net v2 assemblies, so that a fixture can carry real
    /// <c>[Fact]</c> and <c>[Theory]</c> methods of that version.
    /// </summary>
    /// <remarks>
    /// <c>xunit.core</c> reaches every target framework of this suite - through <c>net452</c> on .NET
    /// Framework and through <c>netstandard1.1</c> on .NET - so this set is buildable on all of them and
    /// needs no conditional compilation. Only the v3 side is conditional, and a guard accidentally placed
    /// around this member would silently drop the xUnit.net v2 tests from two target frameworks.
    /// </remarks>
    public static ImmutableArray<MetadataReference> WithXunitV2 => _withXunitV2.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the xUnit.net v3 assemblies, so that a fixture can carry real
    /// <c>[Fact]</c> and <c>[Theory]</c> methods of that version.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The target framework has no xUnit.net v3 assets, which is the case for net6.0 and net7.0. A caller
    /// reaching this member there has an unguarded call site; a set silently falling back to
    /// <see cref="Default" /> would instead turn every test built on it into a false pass.
    /// </exception>
    public static ImmutableArray<MetadataReference> WithXunitV3 =>
#if FRAMESHIFT_XUNIT_V3
        _withXunitV3.Value;
#else
        throw new PlatformNotSupportedException(
            "xUnit.net v3 ships no assets for this target framework, so no reference set can be built for "
                + "it. Guard the call site with '#if FRAMESHIFT_XUNIT_V3'."
        );
#endif

    /// <summary>
    /// Gets <see cref="Default" /> plus the NUnit assemblies, so that a fixture can carry real
    /// <c>[Test]</c> and <c>[TestCase]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithNUnit => _withNUnit.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus the MSTest assemblies, so that a fixture can carry real
    /// <c>[TestMethod]</c> and <c>[DataRow]</c> methods.
    /// </summary>
    public static ImmutableArray<MetadataReference> WithMSTest => _withMSTest.Value;

    /// <summary>
    /// Gets <see cref="Default" /> plus every supported test framework at once, which is what a test
    /// proving that one adapter does not answer for another framework needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two xUnit.net versions declare the very same type names in the very same namespaces. A
    /// fixture built against this set therefore cannot spell out a name such as <c>Xunit.FactAttribute</c>,
    /// and <see cref="Compilation.GetTypeByMetadataName(string)" /> returns <see langword="null" /> for it;
    /// only <c>GetTypesByMetadataName</c> sees both declarations, and only
    /// <see cref="IAssemblySymbol.GetTypeByMetadataName(string)" /> resolves it per assembly. On a target
    /// framework without xUnit.net v3 assets only v2 is part of the set, and that name is then unambiguous
    /// again.
    /// </para>
    /// <para>
    /// That ambiguity is the point of the set, and it must be the only one: no assembly identity may appear
    /// twice, because a duplicate reference makes every lookup ambiguous for a reason that has nothing to do
    /// with the two versions. Two things keep it single. The xUnit.net versions share no assembly identity
    /// at all - v2 contributes <c>xunit.core</c> and <c>xunit.abstractions</c>, v3 contributes
    /// <c>xunit.v3.core</c> and <c>xunit.v3.common</c>, and neither one references anything of the other.
    /// And every path of a set is contributed by an assembly loaded into this very process, so one identity
    /// can only ever resolve to one file, which <see cref="CreateFileReferences" /> then collapses.
    /// </para>
    /// </remarks>
    public static ImmutableArray<MetadataReference> WithAllFrameworks => _withAllFrameworks.Value;

    /// <summary>
    /// Selects <see cref="WithTUnit" /> or <see cref="Default" />.
    /// </summary>
    /// <param name="includeTUnit">Whether the TUnit assemblies are part of the result.</param>
    /// <returns>The selected references.</returns>
    public static ImmutableArray<MetadataReference> For(bool includeTUnit) =>
        For(includeTUnit ? TestFramework.TUnit : TestFramework.None);

    /// <summary>
    /// Selects the reference set of <paramref name="framework" />.
    /// </summary>
    /// <param name="framework">The test framework the compilation is built for.</param>
    /// <returns>The selected references.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="framework" /> is not a known value.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// <paramref name="framework" /> is <see cref="TestFramework.XunitV3" /> on a target framework without
    /// xUnit.net v3 assets; see <see cref="WithXunitV3" />.
    /// </exception>
    public static ImmutableArray<MetadataReference> For(TestFramework framework) =>
        framework switch
        {
            TestFramework.None => Default,
            TestFramework.TUnit => WithTUnit,
            TestFramework.XunitV2 => WithXunitV2,
            TestFramework.XunitV3 => WithXunitV3,
            TestFramework.NUnit => WithNUnit,
            TestFramework.MSTest => WithMSTest,
            TestFramework.All => WithAllFrameworks,
            _ => throw new ArgumentOutOfRangeException(
                nameof(framework),
                framework,
                "There is no reference set for this test framework."
            ),
        };

    /// <summary>
    /// Creates a reference to the assembly declaring <paramref name="type" />, for the rare fixture that
    /// needs a type from a package assembly.
    /// </summary>
    /// <param name="type">The type whose declaring assembly is referenced.</param>
    /// <returns>The created reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The declaring assembly has no file on disk.</exception>
    public static MetadataReference Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var location = type.Assembly.Location;

        if (location.Length == 0)
        {
            throw new InvalidOperationException(
                $"The assembly declaring '{type.FullName}' is not backed by a file and cannot be referenced."
            );
        }

        return MetadataReference.CreateFromFile(location);
    }

    private static ImmutableArray<MetadataReference> CreateDefault() =>
#if NETFRAMEWORK
        // .NET Framework never shipped Span{T}/ReadOnlySpan{T} or ValueTask/ValueTask{TResult}; the
        // System.Memory and System.Threading.Tasks.Extensions packages are what a fixture using any of
        // them needs to resolve them the same way it does on .NET, where they are already part of the
        // runtime the trusted-platform-assemblies path picks up.
        CreateWith([.. SystemMemoryAnchors, .. SystemValueTaskAnchors]);
#else
        CreateReferences(_frameworkPaths.Value);
#endif

    private static ImmutableArray<MetadataReference> CreateWithTUnit() => CreateWith(TUnitAnchors);

    private static ImmutableArray<MetadataReference> CreateWithXunitV2() => CreateWith(XunitV2Anchors);

#if FRAMESHIFT_XUNIT_V3
    private static ImmutableArray<MetadataReference> CreateWithXunitV3() => CreateWith(XunitV3Anchors);
#endif

    private static ImmutableArray<MetadataReference> CreateWithNUnit() => CreateWith(NUnitAnchors);

    private static ImmutableArray<MetadataReference> CreateWithMSTest() => CreateWith(MSTestAnchors);

    /// <summary>
    /// Seeds the walk with every framework anchor, in the order the probe registry reports them, so that the
    /// references of a mixed compilation are listed the way the analyzers walk them.
    /// </summary>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateWithAllFrameworks() =>
        CreateWith([.. TUnitAnchors, .. XunitV2Anchors,
#if FRAMESHIFT_XUNIT_V3
            .. XunitV3Anchors,
#endif
            .. NUnitAnchors, .. MSTestAnchors]);

    /// <summary>
    /// The seed of the TUnit reference set.
    /// </summary>
    private static Type[] TUnitAnchors => [typeof(TUnit.Core.TestAttribute)];

    /// <summary>
    /// The seed of the xUnit.net v2 reference set. <c>Xunit.Sdk.IXunitTestCase</c> lives in
    /// <c>xunit.core</c>, the assembly that also declares the v2 <c>Xunit.FactAttribute</c>, and it has no
    /// counterpart in v3, whose interface is <c>Xunit.v3.IXunitTestCase</c>. The anchor therefore stays
    /// unambiguous while this test project references both versions at once.
    /// </summary>
    private static Type[] XunitV2Anchors => [typeof(Xunit.Sdk.IXunitTestCase)];

#if FRAMESHIFT_XUNIT_V3
    /// <summary>
    /// The seed of the xUnit.net v3 reference set. <c>Xunit.v3.XunitTestFramework</c> lives in
    /// <c>xunit.v3.core</c>, the assembly that also declares the v3 <c>Xunit.FactAttribute</c>, and it has
    /// no counterpart in v2. The anchor therefore stays unambiguous while this test project references both
    /// versions at once.
    /// </summary>
    private static Type[] XunitV3Anchors => [typeof(Xunit.v3.XunitTestFramework)];
#endif

    /// <summary>
    /// The seed of the NUnit reference set.
    /// </summary>
    private static Type[] NUnitAnchors => [typeof(NUnit.Framework.TestAttribute)];

    /// <summary>
    /// The seed of the MSTest reference set.
    /// </summary>
    private static Type[] MSTestAnchors => [typeof(MSTestFramework.TestMethodAttribute)];

#if NETFRAMEWORK
    /// <summary>
    /// The seed of the .NET Framework <c>System.Memory</c> reference, added to <see cref="Default" /> so
    /// that a fixture declaring a <see cref="Span{T}" /> or <see cref="ReadOnlySpan{T}" /> member resolves
    /// on .NET Framework the same way it does on .NET.
    /// </summary>
    private static Type[] SystemMemoryAnchors => [typeof(Span<int>)];

    /// <summary>
    /// The seed of the .NET Framework <c>System.Threading.Tasks.Extensions</c> reference, added to
    /// <see cref="Default" /> so that a fixture declaring a <see cref="ValueTask" /> or
    /// <see cref="ValueTask{TResult}" /> member resolves on .NET Framework the same way it does on .NET.
    /// </summary>
    private static Type[] SystemValueTaskAnchors => [typeof(ValueTask)];
#endif

    /// <summary>
    /// Builds <see cref="Default" /> plus the assembly graph reachable from every anchor.
    /// </summary>
    /// <param name="anchors">The types whose declaring assemblies seed the walk.</param>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateWith(Type[] anchors) =>
        CreateReferences([.. _frameworkPaths.Value, .. GetPackagePaths(anchors)]);

    /// <summary>
    /// Maps every path to a reference and appends the references that are not backed by a file.
    /// </summary>
    /// <param name="paths">The assembly paths to map, in the order they should be referenced.</param>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateReferences(IEnumerable<string> paths) =>
#if NETFRAMEWORK
        [.. CreateFileReferences(paths), .. CompilerSupport];
#else
        CreateFileReferences(paths);
#endif

    /// <summary>
    /// Maps every path to a reference, skipping paths that were already mapped.
    /// </summary>
    /// <param name="paths">The assembly paths to map, in the order they should be referenced.</param>
    /// <returns>The created references.</returns>
    private static ImmutableArray<MetadataReference> CreateFileReferences(IEnumerable<string> paths)
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            builder.Add(MetadataReference.CreateFromFile(path));
        }

        return builder.ToImmutable();
    }

#if NETFRAMEWORK
    /// <summary>
    /// Gets the reference to the compiled <see cref="CompilerSupportSource" />, computed once per process.
    /// </summary>
    private static ImmutableArray<MetadataReference> CompilerSupport => _compilerSupport.Value;

    /// <summary>
    /// Compiles <see cref="CompilerSupportSource" /> into an in-memory assembly, so that the fixtures using
    /// a record compile on .NET Framework without being written differently there.
    /// </summary>
    /// <returns>The single reference to that assembly.</returns>
    /// <exception cref="InvalidOperationException">The compiler-support assembly does not compile.</exception>
    private static ImmutableArray<MetadataReference> CreateCompilerSupport()
    {
        var compilation = CSharpCompilation.Create(
            CompilerSupportAssemblyName,
            [CSharpSyntaxTree.ParseText(CompilerSupportSource)],
            CreateFileReferences(_frameworkPaths.Value),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

            throw new InvalidOperationException(
                $"The compiler-support assembly '{CompilerSupportAssemblyName}' does not compile: "
                    + string.Join("; ", errors)
            );
        }

        return [MetadataReference.CreateFromImage(stream.ToArray())];
    }

    /// <summary>
    /// Selects the .NET Framework runtime assemblies by name, because .NET Framework has no
    /// <c>TRUSTED_PLATFORM_ASSEMBLIES</c> and its runtime directory also holds native images that are no
    /// metadata at all.
    /// </summary>
    /// <returns>The runtime assembly paths.</returns>
    /// <exception cref="InvalidOperationException">A required runtime assembly is not on disk.</exception>
    private static ImmutableArray<string> GetFrameworkPaths()
    {
        var directories = GetFrameworkDirectories();
        var optional = _optionalFrameworkAssemblies
            .Select(name => FindAssembly(directories, name))
            .Where(path => path.Length > 0);

        return [.. GetRequiredFrameworkPaths(directories), .. optional];
    }

    /// <summary>
    /// Resolves <see cref="_requiredFrameworkAssemblies" /> and proves that every one of them was found,
    /// because a reference set missing them would fail every test with a compile error instead of naming
    /// the real cause.
    /// </summary>
    /// <param name="directories">The directories to look in.</param>
    /// <returns>The resolved paths, in the declared order.</returns>
    /// <exception cref="InvalidOperationException">A required runtime assembly is not on disk.</exception>
    private static ImmutableArray<string> GetRequiredFrameworkPaths(ImmutableArray<string> directories)
    {
        var resolved = _requiredFrameworkAssemblies
            .Select(name => (Name: name, Path: FindAssembly(directories, name)))
            .ToImmutableArray();
        var missing = resolved.Where(entry => entry.Path.Length == 0).Select(entry => entry.Name).ToImmutableArray();

        if (!missing.IsEmpty)
        {
            throw new InvalidOperationException(
                $"The .NET Framework runtime directories '{string.Join("', '", directories)}' do not contain "
                    + $"'{string.Join(AssemblyExtension + "', '", missing)}{AssemblyExtension}', "
                    + "so no reference assemblies can be resolved."
            );
        }

        return [.. resolved.Select(entry => entry.Path)];
    }

    /// <summary>
    /// The directories the runtime assemblies are looked up in: the one the CLR reports and the one
    /// <c>mscorlib</c> was loaded from. They are the same directory in a normal process, and both are
    /// consulted so that a shadow-copying host still resolves.
    /// </summary>
    /// <returns>The directories, without a trailing separator and without duplicates.</returns>
    private static ImmutableArray<string> GetFrameworkDirectories()
    {
        var location = typeof(object).Assembly.Location;
        var directories = new List<string>(2) { RuntimeEnvironment.GetRuntimeDirectory() };

        if (location.Length > 0)
        {
            directories.Add(Path.GetDirectoryName(location) ?? string.Empty);
        }

        return
        [
            .. directories
                .Where(directory => directory.Length > 0)
                .Select(directory => directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Finds an assembly file by its simple name.
    /// </summary>
    /// <param name="directories">The directories to look in, in order.</param>
    /// <param name="name">The simple name of the assembly.</param>
    /// <returns>The path of the first match, or an empty string when there is none.</returns>
    private static string FindAssembly(ImmutableArray<string> directories, string name)
    {
        var fileName = name + AssemblyExtension;

        return directories.Select(directory => Path.Combine(directory, fileName)).FirstOrDefault(File.Exists)
            ?? string.Empty;
    }

    /// <summary>
    /// Decides whether a walked assembly is part of the framework itself. On .NET Framework the framework
    /// assemblies are loaded from the assembly cache, whose paths differ from the runtime directory ones
    /// <see cref="GetFrameworkPaths" /> resolved. Referencing both copies would make every type of
    /// <c>System</c> ambiguous, so the cached copy is dropped.
    /// </summary>
    /// <param name="assembly">The walked assembly.</param>
    /// <returns><see langword="true" /> when the assembly is already covered by the framework paths.</returns>
    private static bool IsFrameworkAssembly(Assembly assembly) =>
        assembly.GlobalAssemblyCache
        || _frameworkPaths.Value.Any(path =>
            string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(assembly.Location),
                StringComparison.OrdinalIgnoreCase
            )
        );
#else
    /// <summary>
    /// Selects the trusted platform assemblies that live next to the runtime itself.
    /// </summary>
    /// <returns>
    /// The runtime assembly paths, or all trusted platform assemblies when the runtime directory cannot
    /// be determined, which happens for a single-file host.
    /// </returns>
    private static ImmutableArray<string> GetFrameworkPaths()
    {
        var all = GetTrustedPlatformAssemblies();
        var location = typeof(object).Assembly.Location;
        var runtimeDirectory = location.Length == 0 ? null : Path.GetDirectoryName(location);
        var framework = all.Where(path => IsInDirectory(path, runtimeDirectory)).ToImmutableArray();

        return framework.IsEmpty ? all : framework;
    }

    /// <summary>
    /// Reads and splits the trusted platform assemblies of the current process.
    /// </summary>
    /// <returns>The paths of all trusted assemblies that are managed libraries.</returns>
    /// <exception cref="InvalidOperationException">The host did not publish the list.</exception>
    private static ImmutableArray<string> GetTrustedPlatformAssemblies()
    {
        if (AppContext.GetData(TrustedPlatformAssembliesKey) is not string value || value.Length == 0)
        {
            throw new InvalidOperationException(
                $"The current process does not publish '{TrustedPlatformAssembliesKey}', "
                    + "so no reference assemblies can be resolved."
            );
        }

        return
        [
            .. value
                .Split(Path.PathSeparator)
                .Where(path => path.EndsWith(AssemblyExtension, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static bool IsInDirectory(string path, string? directory) =>
        directory is { Length: > 0 }
        && string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase);
#endif

    /// <summary>
    /// Walks the assembly reference graph of every anchor's declaring assembly, so that the anchor types
    /// and every type they need can be bound by a test compilation.
    /// </summary>
    /// <param name="anchors">The types whose declaring assemblies seed the walk.</param>
    /// <returns>The paths of the reachable assemblies that are backed by a file.</returns>
    /// <exception cref="InvalidOperationException">An anchor assembly is not backed by a file.</exception>
    private static ImmutableArray<string> GetPackagePaths(Type[] anchors)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        var builder = ImmutableArray.CreateBuilder<string>();

        foreach (var anchor in anchors)
        {
            pending.Enqueue(GetAnchorAssembly(anchor));
        }

        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();

            if (!visited.Add(assembly.FullName ?? assembly.ToString()))
            {
                continue;
            }

#if NETFRAMEWORK
            if (IsFrameworkAssembly(assembly))
            {
                continue;
            }
#endif

            if (assembly.Location.Length > 0)
            {
                builder.Add(assembly.Location);
            }

            EnqueueReferences(assembly, pending);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Resolves the assembly of an anchor type and proves it is backed by a file, because a reference set
    /// that silently lost its framework would turn every test using it into a false pass.
    /// </summary>
    /// <param name="anchor">The type whose declaring assembly seeds the walk.</param>
    /// <returns>The declaring assembly.</returns>
    /// <exception cref="InvalidOperationException">The declaring assembly has no file on disk.</exception>
    private static Assembly GetAnchorAssembly(Type anchor)
    {
        var assembly = anchor.Assembly;

        if (assembly.Location.Length == 0)
        {
            throw new InvalidOperationException(
                $"The assembly declaring '{anchor.FullName}' is not backed by a file and cannot be referenced."
            );
        }

        return assembly;
    }

    private static void EnqueueReferences(Assembly assembly, Queue<Assembly> pending)
    {
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            var referenced = TryLoad(name);

            if (referenced is not null)
            {
                pending.Enqueue(referenced);
            }
        }
    }

    private static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
