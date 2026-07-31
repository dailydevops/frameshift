namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the framework-neutral half of the discovery: which method declarations are offered to a
/// recogniser at all, and what happens around the ones that cannot be bound to a method symbol.
/// </summary>
/// <remarks>
/// The recogniser is a stub here on purpose. Which methods count is the business of a framework probe
/// and is covered by its own tests; what this class pins down is the walk, the de-duplication and the
/// declaration order the whole test surface depends on.
/// </remarks>
public class TestMethodDiscoveryTests
{
    private const string FirstPath = "First.cs";
    private const string SecondPath = "Second.cs";

    private const string FirstSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class CaseAttribute : Attribute
        {
        }

        public class FirstCases
        {
            [Case]
            public void Alpha()
            {
            }

            public void Undecorated()
            {
            }
        }
        """;

    private const string SecondSource = """
        namespace Fixture;

        public class SecondCases
        {
            [Case]
            public void Beta()
            {
            }
        }
        """;

    /// <summary>
    /// A method declared where no type can contain it. The compiler reports the declaration as an error,
    /// which is the state the discovery has to walk over without throwing.
    /// </summary>
    private const string OrphanedMethodSource = """
        namespace Fixture;

        [Case]
        public void Orphaned()
        {
        }
        """;

    private const string OrphanPath = "Orphan.cs";
    private const string OrphanedMethodName = "Orphaned";

    /// <summary>
    /// A test method carrying a local function that is decorated exactly like a test. Only method
    /// declarations are walked, and a local function is none, so the recogniser is never even asked
    /// about it.
    /// </summary>
    private const string LocalFunctionSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class CaseAttribute : Attribute
        {
        }

        public class LocalFunctionCases
        {
            [Case]
            public void Outer()
            {
                [Case]
                void Inner()
                {
                }

                Inner();
            }
        }
        """;

    /// <summary>
    /// A test method split into a defining and an implementing declaration. Both parts carry the merged
    /// attributes of the method, so a discovery that offered every declaration on its own would hand the
    /// same test to the analysis twice.
    /// </summary>
    private const string PartialMethodSource = """
        namespace Fixture;

        using System;

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class CaseAttribute : Attribute
        {
        }

        public partial class PartialCases
        {
            [Case]
            public partial void Alpha();

            public partial void Alpha()
            {
            }
        }
        """;

    [Test]
    public async Task Fixtures_TheTwoFileCompilation_CompilesWithoutErrors()
    {
        var compilation = CreateCompilation();

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task FindTestMethods_CompilationIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = TestMethodDiscovery.FindTestMethods(null!, new AttributeNameRecognizer(), CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("compilation");
    }

    [Test]
    public async Task FindTestMethods_RecognizerIsNull_ThrowsArgumentNullException()
    {
        var compilation = CreateCompilation();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            _ = TestMethodDiscovery.FindTestMethods(compilation, null!, CancellationToken.None)
        );

        _ = await Assert.That(exception.ParamName).IsEqualTo("recognizer");
    }

    [Test]
    public async Task FindTestMethods_SeveralFiles_AreWalkedInDeclarationOrder()
    {
        var found = TestMethodDiscovery.FindTestMethods(
            CreateCompilation(),
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert.That(Describe(found)).IsEqualTo("FirstCases.Alpha|SecondCases.Beta");
    }

    /// <summary>
    /// A recogniser that accepts nothing must yield nothing, which is the state every analyzer treats as
    /// "this compilation is none of my business".
    /// </summary>
    [Test]
    public async Task FindTestMethods_RecognizerAcceptsNothing_ReturnsEmpty()
    {
        var found = TestMethodDiscovery.FindTestMethods(
            CreateCompilation(),
            new RejectingRecognizer(),
            CancellationToken.None
        );

        _ = await Assert.That(found).IsEmpty();
    }

    /// <summary>
    /// The discovery runs inside an analyzer, therefore on code that is being typed and does not compile.
    /// A declaration the compiler cannot place in a type must never make it throw.
    /// </summary>
    [Test]
    public async Task FindTestMethods_DeclarationOutsideAnyType_IsSurvivedAndTheOtherTestsAreStillFound()
    {
        var compilation = CompilationFactory.Create([
            (FirstPath, FirstSource),
            (SecondPath, SecondSource),
            ("Orphan.cs", OrphanedMethodSource),
        ]);

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert.That(CompilationFactory.GetCompileErrors(compilation).Length).IsGreaterThan(0);
        _ = await Assert.That(Describe(found)).Contains("FirstCases.Alpha");
        _ = await Assert.That(Describe(found)).Contains("SecondCases.Beta");
    }

    /// <summary>
    /// The same fixture, asserted from the other side: the declaration outside any type is not skipped
    /// either. The compiler places such a member in a container it synthesises for it, so the declaration
    /// does have a method symbol and the discovery hands it to the recogniser like any other.
    /// </summary>
    /// <remarks>
    /// This is what keeps the guard against a declaration without a symbol from being reachable through
    /// invalid placement: even the most misplaced method declaration binds.
    /// </remarks>
    [Test]
    public async Task FindTestMethods_DeclarationOutsideAnyType_IsBoundAndDiscoveredLikeAnyOther()
    {
        var compilation = CreateCompilationWithOrphan();

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert.That(found.Length).IsEqualTo(3);
        _ = await Assert.That(found[2].Name).IsEqualTo(OrphanedMethodName);
        _ = await Assert.That(found[2].ContainingType).IsNotNull();
    }

    /// <summary>
    /// Every discovered method is declared by a method declaration of the analysed source, which is the
    /// invariant the reporting side relies on: it resolves the location of a finding from exactly that
    /// declaration. A symbol from metadata, or one without a declaration, can therefore never reach it.
    /// </summary>
    [Test]
    public async Task FindTestMethods_EveryDiscoveredMethod_IsDeclaredByAMethodDeclarationInSource()
    {
        var found = TestMethodDiscovery.FindTestMethods(
            CreateCompilationWithOrphan(),
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert.That(found.Length).IsEqualTo(3);
        _ = await Assert.That(found.All(HasMethodDeclarationInSource)).IsTrue();
    }

    /// <summary>
    /// A local function is not a method declaration, so it is not offered to the recogniser at all, no
    /// matter how it is decorated. Its containing test method still is.
    /// </summary>
    [Test]
    public async Task FindTestMethods_DecoratedLocalFunction_IsNotDiscovered()
    {
        var compilation = CompilationFactory.Create(LocalFunctionSource);

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(Describe(found)).IsEqualTo("LocalFunctionCases.Outer");
    }

    /// <summary>
    /// A partial test method is one test, however many declarations it is written in, so it is discovered
    /// exactly once and by its defining declaration.
    /// </summary>
    [Test]
    public async Task FindTestMethods_PartialTestMethod_IsDiscoveredOnceForBothDeclarations()
    {
        var compilation = CompilationFactory.Create(PartialMethodSource);

        var found = TestMethodDiscovery.FindTestMethods(
            compilation,
            new AttributeNameRecognizer(),
            CancellationToken.None
        );

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
        _ = await Assert.That(Describe(found)).IsEqualTo("PartialCases.Alpha");
        _ = await Assert.That(HasMethodDeclarationInSource(found[0])).IsTrue();
    }

    [Test]
    public async Task FindTestMethods_CancelledToken_ThrowsOperationCanceledException()
    {
        var compilation = CreateCompilation();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsyncCompat().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = TestMethodDiscovery.FindTestMethods(compilation, new AttributeNameRecognizer(), cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static CSharpCompilation CreateCompilation() =>
        CompilationFactory.Create([(FirstPath, FirstSource), (SecondPath, SecondSource)]);

    private static CSharpCompilation CreateCompilationWithOrphan() =>
        CompilationFactory.Create([
            (FirstPath, FirstSource),
            (SecondPath, SecondSource),
            (OrphanPath, OrphanedMethodSource),
        ]);

    /// <summary>
    /// Determines whether <paramref name="method" /> is declared by a method declaration of the analysed
    /// source, which is what the reporting side resolves the location of a finding from.
    /// </summary>
    /// <param name="method">The discovered method.</param>
    /// <returns><see langword="true" /> when the declaration is there; otherwise <see langword="false" />.</returns>
    private static bool HasMethodDeclarationInSource(IMethodSymbol method) =>
        method
            .DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(CancellationToken.None))
            .OfType<MethodDeclarationSyntax>()
            .Any() && method.Locations.Any(location => location.IsInSource);

    private static string Describe(IEnumerable<IMethodSymbol> methods) =>
        string.Join("|", methods.Select(method => method.ContainingType.Name + "." + method.Name));

    /// <summary>
    /// Accepts every method carrying an attribute whose type is named <c>CaseAttribute</c>.
    /// </summary>
    private sealed class AttributeNameRecognizer : ITestMethodRecognizer
    {
        public string FrameworkName => "Fixture";

        public bool IsTestMethod(IMethodSymbol method) =>
            method.GetAttributes().Any(attribute => IsCaseAttribute(attribute.AttributeClass));

        private static bool IsCaseAttribute(INamedTypeSymbol? attributeClass) =>
            attributeClass is not null && string.Equals(attributeClass.Name, "CaseAttribute", StringComparison.Ordinal);
    }

    /// <summary>
    /// Accepts no method at all.
    /// </summary>
    private sealed class RejectingRecognizer : ITestMethodRecognizer
    {
        public string FrameworkName => "Fixture";

        public bool IsTestMethod(IMethodSymbol method) => false;
    }
}
