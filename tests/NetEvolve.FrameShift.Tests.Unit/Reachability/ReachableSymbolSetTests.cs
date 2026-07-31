namespace NetEvolve.FrameShift.Tests.Unit;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.Tests.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the set itself, meaning the normalization every symbol goes through on the way in and the two
/// lookups the production side analyzer asks it: whether a symbol is reachable, and whether anything
/// enclosing it is.
/// </summary>
/// <remarks>
/// The behaviour under test is what decides whether a mutation point is reported as uncovered, so a
/// lookup that answers "no" too eagerly invents a gap and one that answers "yes" too eagerly hides one.
/// </remarks>
public class ReachableSymbolSetTests
{
    private const string SetSource = """
        namespace Production;

        public static class Extensions
        {
            public static int Doubled(this int value) => value * 2;
        }

        public sealed class Holder
        {
            public int Value { get; set; }

            public int Untested() => 0;

            public int Nested()
            {
                return Outer();

                static int Outer()
                {
                    return Inner();

                    static int Inner() => 3;
                }
            }
        }

        public static class Consumer
        {
            public static int Use(int value) => /*!*/value.Doubled();
        }
        """;

    [Test]
    public async Task Fixture_TheCompilation_CompilesWithoutErrors()
    {
        var compilation = CompilationFactory.Create(SetSource);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Constructor_SymbolsAreNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = new ReachableSymbolSet(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("symbols");
    }

    [Test]
    public async Task Constructor_NullEntriesAndRepetitions_AreDropped()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var untested = Method(compilation, "Production.Holder", "Untested");

        var set = new ReachableSymbolSet([null!, untested, untested]);

        _ = await Assert.That(set.Count).IsEqualTo(1);
        _ = await Assert.That(set.Contains(untested)).IsTrue();
    }

    [Test]
    public async Task Empty_TheSharedSet_HoldsNothing()
    {
        var set = ReachableSymbolSet.Empty;

        _ = await Assert.That(set.IsEmpty).IsTrue();
        _ = await Assert.That(set.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IsEmpty_PopulatedSet_ReturnsFalse()
    {
        var compilation = CompilationFactory.Create(SetSource);

        var set = new ReachableSymbolSet([
            Method(compilation, "Production.Holder", "Untested"),
            Method(compilation, "Production.Holder", "Nested"),
        ]);

        _ = await Assert.That(set.IsEmpty).IsFalse();
        _ = await Assert.That(set.Count).IsEqualTo(2);
    }

    [Test]
    public async Task NormalizeDefinition_SymbolIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = ReachableSymbolSet.NormalizeDefinition(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("symbol");
    }

    /// <summary>
    /// An extension method invoked in reduced form is a different symbol than the one that is declared.
    /// Recording the reduced form would make the declaration look untested.
    /// </summary>
    [Test]
    public async Task NormalizeDefinition_ReducedExtensionMethod_ReturnsTheDeclaredMethod()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(SetSource);
        var reduced = ReducedExtensionInvocation(semanticModel, tree);
        var declared = Method(compilation, "Production.Extensions", "Doubled");

        var normalized = ReachableSymbolSet.NormalizeDefinition(reduced);

        _ = await Assert.That(reduced.ReducedFrom).IsNotNull();
        _ = await Assert.That(SymbolEqualityComparer.Default.Equals(normalized, declared)).IsTrue();
    }

    [Test]
    public async Task Contains_ReducedExtensionMethod_FindsTheRecordedDeclaration()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(SetSource);
        var reduced = ReducedExtensionInvocation(semanticModel, tree);
        var set = new ReachableSymbolSet([Method(compilation, "Production.Extensions", "Doubled")]);

        _ = await Assert.That(set.Contains(reduced)).IsTrue();
    }

    [Test]
    public async Task Contains_SymbolIsNull_ThrowsArgumentNullException()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Untested")]);

        var exception = Assert.Throws<ArgumentNullException>(() => _ = set.Contains(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("symbol");
    }

    [Test]
    public async Task Contains_MemberThatWasNeverRecorded_ReturnsFalse()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Nested")]);

        _ = await Assert.That(set.Contains(Method(compilation, "Production.Holder", "Untested"))).IsFalse();
    }

    /// <summary>
    /// A reference to <c>Value</c> and a reference to <c>get_Value</c> describe the same test surface, so
    /// an accessor of a recorded property counts as recorded too.
    /// </summary>
    [Test]
    public async Task Contains_AccessorOfARecordedProperty_ReturnsTrue()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var property = Property(compilation, "Production.Holder", "Value");
        var set = new ReachableSymbolSet([property]);

        _ = await Assert.That(set.Contains(property.GetMethod!)).IsTrue();
        _ = await Assert.That(set.Contains(property.SetMethod!)).IsTrue();
    }

    [Test]
    public async Task Contains_AccessorOfAPropertyThatWasNeverRecorded_ReturnsFalse()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var property = Property(compilation, "Production.Holder", "Value");
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Untested")]);

        _ = await Assert.That(set.Contains(property.GetMethod!)).IsFalse();
    }

    [Test]
    public async Task ContainsEnclosing_SymbolIsNull_ReturnsFalseEvenForAPopulatedSet()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Nested")]);

        _ = await Assert.That(set.IsEmpty).IsFalse();
        _ = await Assert.That(set.ContainsEnclosing(null)).IsFalse();
    }

    [Test]
    public async Task ContainsEnclosing_SymbolItselfIsRecorded_ReturnsTrue()
    {
        var compilation = CompilationFactory.Create(SetSource);
        var nested = Method(compilation, "Production.Holder", "Nested");
        var set = new ReachableSymbolSet([nested]);

        _ = await Assert.That(set.ContainsEnclosing(nested)).IsTrue();
    }

    /// <summary>
    /// The walk climbs through as many nested local functions as it takes, so a mutation two levels deep
    /// is still attributed to the member a test actually reaches.
    /// </summary>
    [Test]
    public async Task ContainsEnclosing_NestedLocalFunction_IsAttributedToTheEnclosingMember()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(SetSource);
        var inner = LocalFunction(semanticModel, tree, "Inner");
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Nested")]);

        _ = await Assert.That(set.Contains(inner)).IsFalse();
        _ = await Assert.That(set.ContainsEnclosing(inner)).IsTrue();
    }

    [Test]
    public async Task ContainsEnclosing_ChainEndsWithoutAMatch_ReturnsFalse()
    {
        var (compilation, semanticModel, tree) = CompilationFactory.CreateWithModel(SetSource);
        var inner = LocalFunction(semanticModel, tree, "Inner");
        var set = new ReachableSymbolSet([Method(compilation, "Production.Holder", "Untested")]);

        _ = await Assert.That(set.ContainsEnclosing(inner)).IsFalse();
    }

    private static IMethodSymbol ReducedExtensionInvocation(SemanticModel semanticModel, SyntaxTree tree)
    {
        var invocation = SyntaxNodeLocator.FindMarked<InvocationExpressionSyntax>(tree);

        return (IMethodSymbol)semanticModel.GetSymbolInfo(invocation).Symbol!;
    }

    private static ISymbol LocalFunction(SemanticModel semanticModel, SyntaxTree tree, string name)
    {
        var declaration = SyntaxNodeLocator.FindFirst<LocalFunctionStatementSyntax>(
            tree,
            node => string.Equals(node.Identifier.ValueText, name, StringComparison.Ordinal)
        );

        return semanticModel.GetDeclaredSymbol(declaration)!;
    }

    private static IMethodSymbol Method(Compilation compilation, string typeName, string methodName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(methodName).OfType<IMethodSymbol>().First();

    private static IPropertySymbol Property(Compilation compilation, string typeName, string propertyName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(propertyName).OfType<IPropertySymbol>().First();
}
