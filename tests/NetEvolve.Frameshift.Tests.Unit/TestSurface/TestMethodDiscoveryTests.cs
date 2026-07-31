namespace NetEvolve.Frameshift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.Frameshift.Tests.Infrastructure;
using NetEvolve.Frameshift.TestSurface;
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

    [Test]
    public async Task FindTestMethods_CancelledToken_ThrowsOperationCanceledException()
    {
        var compilation = CreateCompilation();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);

        var exception = Assert.Throws<OperationCanceledException>(() =>
            _ = TestMethodDiscovery.FindTestMethods(compilation, new AttributeNameRecognizer(), cancellation.Token)
        );

        _ = await Assert.That(exception).IsNotNull();
    }

    private static CSharpCompilation CreateCompilation() =>
        CompilationFactory.Create([(FirstPath, FirstSource), (SecondPath, SecondSource)]);

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
