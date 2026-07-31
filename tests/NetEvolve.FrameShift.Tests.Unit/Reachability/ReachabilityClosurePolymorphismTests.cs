namespace NetEvolve.FrameShift.Tests.Unit;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetEvolve.FrameShift.Reachability;
using NetEvolve.FrameShift.Tests.Infrastructure;
using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the part of the reachability closure that resolves polymorphism: the override chain walk,
/// the interface implementation search and the normalisation to the original definition that makes
/// both of them work for generic types and generic methods.
/// </summary>
/// <remarks>
/// <para>
/// Polymorphism is the rule and not the exception in production code, so every member that the
/// closure fails to connect to the abstraction it is called through turns into a false gap report.
/// The fixtures therefore always let the override or the implementation call a private helper of its
/// own: only if the body of the resolved member is actually walked does that helper become reachable,
/// which makes "the override was found" and "the override was expanded" two separately provable facts.
/// </para>
/// <para>
/// The negative cases matter just as much. A member that merely shares a name with the abstraction,
/// a member that hides it with <c>new</c> instead of overriding it, and an override of a base that
/// nobody touched must all stay out of the reachable set, because a set that grows too eagerly hides
/// exactly the gaps the analysis exists to find.
/// </para>
/// </remarks>
public class ReachabilityClosurePolymorphismTests
{
    private const string VirtualSource = """
        namespace Production;

        public class Renderer
        {
            public virtual string Render() => "base";
        }

        public sealed class HtmlRenderer : Renderer
        {
            public override string Render() => Decorate("html");

            private string Decorate(string value) => "<" + value + ">";
        }

        public sealed class Shadowed : Renderer
        {
            public new string Render() => Hidden();

            private string Hidden() => "hidden";
        }

        public class Untouched
        {
            public virtual string Idle() => "idle";
        }

        public sealed class UntouchedOverride : Untouched
        {
            public override string Idle() => Never();

            private string Never() => "never";
        }

        public static class Names
        {
            public const string Render = "render";
        }

        public static class VirtualConsumer
        {
            public static string Use(Renderer renderer) => renderer.Render();
        }
        """;

    private const string OverrideChainSource = """
        namespace Production;

        public abstract class LayerBase
        {
            public virtual string Describe() => "base";
        }

        public class MiddleLayer : LayerBase
        {
            public override string Describe() => "middle";
        }

        public sealed class LeafLayer : MiddleLayer
        {
            public override string Describe() => Detail();

            private string Detail() => "leaf";
        }

        public static class LayerConsumer
        {
            public static string UseBase(LayerBase layer) => layer.Describe();

            public static string UseMiddle(MiddleLayer layer) => layer.Describe();
        }
        """;

    private const string AccessorSource = """
        namespace Production;

        public abstract class SettingsBase
        {
            public abstract string Name { get; }

            public abstract event System.EventHandler Changed;
        }

        public sealed class Settings : SettingsBase
        {
            public override string Name => Compose();

            public override event System.EventHandler Changed
            {
                add => Track();
                remove => Track();
            }

            private string Compose() => "settings";

            private void Track()
            {
            }
        }

        public static class SettingsConsumer
        {
            public static string UseProperty(SettingsBase settings) => settings.Name;

            public static void UseEvent(SettingsBase settings) => settings.Changed += Handle;

            private static void Handle(object? sender, System.EventArgs args)
            {
            }
        }
        """;

    private const string AbstractSource = """
        namespace Production;

        public abstract class ValidatorBase
        {
            public abstract bool IsValid();
        }

        public sealed class AlwaysValid : ValidatorBase
        {
            public override bool IsValid() => Accept();

            private bool Accept() => true;
        }

        public sealed class NeverValid : ValidatorBase
        {
            public override bool IsValid() => Reject();

            private bool Reject() => false;
        }

        public abstract class TerminalBase
        {
            public abstract void Stop();
        }

        public class Terminal : TerminalBase
        {
            public sealed override void Stop() => Cleanup();

            private void Cleanup()
            {
            }
        }

        public static class AbstractConsumer
        {
            public static bool UseValidator(ValidatorBase validator) => validator.IsValid();

            public static void UseTerminal(TerminalBase terminal) => terminal.Stop();
        }
        """;

    private const string InterfaceSource = """
        namespace Production;

        public interface IHandler
        {
            void Handle();

            void Reset();
        }

        public sealed class ImplicitHandler : IHandler
        {
            public void Handle() => Log();

            public void Reset() => Log();

            private void Log()
            {
            }
        }

        public sealed class ExplicitHandler : IHandler
        {
            void IHandler.Handle() => Trace();

            void IHandler.Reset() => Trace();

            private void Trace()
            {
            }
        }

        public interface IStarter
        {
            void Start();
        }

        public class StarterBase
        {
            public void Start() => Prepare();

            protected void Prepare()
            {
            }
        }

        public sealed class Starter : StarterBase, IStarter
        {
        }

        public static class InterfaceConsumer
        {
            public static void UseHandler(IHandler handler) => handler.Handle();

            public static void UseStarter(IStarter starter) => starter.Start();
        }
        """;

#if !NETFRAMEWORK
    /// <summary>
    /// A default interface implementation, which the compiler only accepts when the referenced runtime
    /// declares support for it. The reference assemblies of .NET Framework do not, so this fixture and the
    /// expectation built on it exist on the modern targets only.
    /// </summary>
    private const string DefaultImplementationSource = """
        namespace Production;

        public interface IPinger
        {
            void Ping() => Pong();

            void Pong();
        }

        public sealed class Pinger : IPinger
        {
            public void Pong() => Track();

            private void Track()
            {
            }
        }

        public static class DefaultImplementationConsumer
        {
            public static void Use(IPinger pinger) => pinger.Ping();
        }
        """;
#endif

    private const string GenericSource = """
        namespace Production;

        public interface IRepository<TEntity>
        {
            TEntity Get(int id);
        }

        public sealed class StringRepository : IRepository<string>
        {
            public string Get(int id) => Load(id);

            private string Load(int id) => "value";
        }

        public sealed class Int32Repository : IRepository<int>
        {
            public int Get(int id) => Count(id);

            private int Count(int id) => id;
        }

        public abstract class MapperBase
        {
            public abstract TResult Map<TResult>(string value);
        }

        public sealed class Mapper : MapperBase
        {
            public override TResult Map<TResult>(string value) => Create<TResult>();

            private static TResult Create<TResult>() => default!;
        }

        public static class GenericConsumer
        {
            public static string UseRepository(IRepository<string> repository) => repository.Get(1);

            public static int UseMapper(MapperBase mapper) => mapper.Map<int>("value");
        }
        """;

    private const string BaseConstructorSource = """
        namespace Production;

        public class DocumentBase
        {
            protected DocumentBase(string title) => Title = title;

            public string Title { get; }

            public virtual string Render() => Title;
        }

        public sealed class Report : DocumentBase
        {
            public Report()
                : base(Compose())
            {
            }

            private static string Compose() => "report";
        }
        """;

    private const string TypeReferenceSource = """
        namespace Production;

        public static class TypeReferences
        {
            public static System.Type ArrayType() => typeof(string[]);
        }
        """;

    private const string ExternalSource = """
        namespace External;

        public interface IExternalWorker
        {
            void Work();
        }

        public abstract class ExternalWorkerBase : IExternalWorker
        {
            public void Work()
            {
            }
        }
        """;

    private const string ExternalConsumerSource = """
        namespace Production;

        using External;

        public sealed class LocalWorker : ExternalWorkerBase
        {
        }

        public static class ExternalConsumer
        {
            public static void Use(IExternalWorker worker) => worker.Work();
        }
        """;

    private const string ExternalAssemblyName = "ExternalAssembly";

    private const string BaseTestId = "M:Tests.LayerTests.EntersAtTheBase";
    private const string MiddleTestId = "M:Tests.LayerTests.EntersInTheMiddle";

    [Test]
    [Arguments(VirtualSource)]
    [Arguments(OverrideChainSource)]
    [Arguments(AccessorSource)]
    [Arguments(AbstractSource)]
    [Arguments(InterfaceSource)]
#if !NETFRAMEWORK
    [Arguments(DefaultImplementationSource)]
#endif
    [Arguments(GenericSource)]
    [Arguments(BaseConstructorSource)]
    [Arguments(TypeReferenceSource)]
    [Arguments(ExternalSource)]
    public async Task Fixtures_EveryCompilation_CompilesWithoutErrors(string source)
    {
        var compilation = CompilationFactory.Create(source);

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Fixtures_ExternalConsumerCompilation_CompilesWithoutErrors()
    {
        var compilation = CreateExternalConsumerCompilation();

        _ = await Assert
            .That(DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation)))
            .IsEqualTo(DiagnosticAssertions.NoDiagnostics);
    }

    [Test]
    public async Task Compute_VirtualMethodWithOneOverride_ReachesTheOverrideAndItsHelper()
    {
        var compilation = CompilationFactory.Create(VirtualSource);
        var manifest = Manifest("M:Production.VirtualConsumer.Use(Production.Renderer)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Renderer", "Render"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.HtmlRenderer", "Render")))
                .IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.HtmlRenderer", "Decorate")))
                .IsTrue();
        }
    }

    [Test]
    public async Task Compute_MemberHidingTheVirtualMethodWithNew_StaysUnreachable()
    {
        var compilation = CompilationFactory.Create(VirtualSource);
        var manifest = Manifest("M:Production.VirtualConsumer.Use(Production.Renderer)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Shadowed", "Render"))).IsFalse();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Shadowed", "Hidden"))).IsFalse();
        }
    }

    [Test]
    public async Task Compute_MemberOfAnotherKindWithTheSameName_StaysUnreachable()
    {
        var compilation = CompilationFactory.Create(VirtualSource);
        var manifest = Manifest("M:Production.VirtualConsumer.Use(Production.Renderer)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Names", "Render"))).IsFalse();
    }

    [Test]
    public async Task Compute_OverrideOfAnUntouchedBase_StaysUnreachable()
    {
        var compilation = CompilationFactory.Create(VirtualSource);
        var manifest = Manifest("M:Production.VirtualConsumer.Use(Production.Renderer)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Untouched", "Idle"))).IsFalse();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.UntouchedOverride", "Idle")))
                .IsFalse();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.UntouchedOverride", "Never")))
                .IsFalse();
        }
    }

    [Test]
    public async Task Compute_TwoLevelOverrideChainSeededAtTheBase_ReachesEveryLevel()
    {
        var compilation = CompilationFactory.Create(OverrideChainSource);
        var manifest = Manifest("M:Production.LayerConsumer.UseBase(Production.LayerBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.LayerBase", "Describe"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.MiddleLayer", "Describe")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.LeafLayer", "Describe"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.LeafLayer", "Detail"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_TwoLevelOverrideChainSeededInTheMiddle_ReachesDownwardsOnly()
    {
        var compilation = CompilationFactory.Create(OverrideChainSource);
        var manifest = Manifest("M:Production.LayerConsumer.UseMiddle(Production.MiddleLayer)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.MiddleLayer", "Describe")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.LeafLayer", "Describe"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.LayerBase", "Describe")))
                .IsFalse();
        }
    }

    [Test]
    public async Task Compute_OverriddenProperty_ReachesTheOverridingAccessorBody()
    {
        var compilation = CompilationFactory.Create(AccessorSource);
        var manifest = Manifest("M:Production.SettingsConsumer.UseProperty(Production.SettingsBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.SettingsBase", "Name"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Name"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Compose"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_OverriddenEvent_ReachesTheOverridingAccessorBody()
    {
        var compilation = CompilationFactory.Create(AccessorSource);
        var manifest = Manifest("M:Production.SettingsConsumer.UseEvent(Production.SettingsBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.SettingsBase", "Changed")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Changed"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Track"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_OverriddenEvent_DoesNotReachTheUnrelatedPropertyOverride()
    {
        var compilation = CompilationFactory.Create(AccessorSource);
        var manifest = Manifest("M:Production.SettingsConsumer.UseEvent(Production.SettingsBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Name"))).IsFalse();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Settings", "Compose"))).IsFalse();
        }
    }

    [Test]
    public async Task Compute_AbstractMemberWithTwoImplementations_ReachesBothOfThem()
    {
        var compilation = CompilationFactory.Create(AbstractSource);
        var manifest = Manifest("M:Production.AbstractConsumer.UseValidator(Production.ValidatorBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.AlwaysValid", "IsValid")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.AlwaysValid", "Accept"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.NeverValid", "IsValid"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.NeverValid", "Reject"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_SealedOverride_IsReachedLikeAnyOtherOverride()
    {
        var compilation = CompilationFactory.Create(AbstractSource);
        var manifest = Manifest("M:Production.AbstractConsumer.UseTerminal(Production.TerminalBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.TerminalBase", "Stop"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Terminal", "Stop"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Terminal", "Cleanup"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_InterfaceMember_ReachesTheImplicitAndTheExplicitImplementation()
    {
        var compilation = CompilationFactory.Create(InterfaceSource);
        var manifest = Manifest("M:Production.InterfaceConsumer.UseHandler(Production.IHandler)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IHandler", "Handle"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ImplicitHandler", "Handle")))
                .IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ImplicitHandler", "Log")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(ExplicitImplementation(compilation, "Handle"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ExplicitHandler", "Trace")))
                .IsTrue();
        }
    }

    [Test]
    public async Task Compute_InterfaceMemberThatIsNeverCalled_StaysUnreachableWithItsImplementations()
    {
        var compilation = CompilationFactory.Create(InterfaceSource);
        var manifest = Manifest("M:Production.InterfaceConsumer.UseHandler(Production.IHandler)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IHandler", "Reset"))).IsFalse();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ImplicitHandler", "Reset")))
                .IsFalse();
            _ = await Assert.That(reachable.Contains(ExplicitImplementation(compilation, "Reset"))).IsFalse();
        }
    }

    [Test]
    public async Task Compute_InterfaceImplementedByABaseClass_ReachesTheInheritedImplementation()
    {
        var compilation = CompilationFactory.Create(InterfaceSource);
        var manifest = Manifest("M:Production.InterfaceConsumer.UseStarter(Production.IStarter)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IStarter", "Start"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.StarterBase", "Start"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.StarterBase", "Prepare")))
                .IsTrue();
        }
    }

#if !NETFRAMEWORK
    [Test]
    public async Task Compute_InterfaceMemberWithADefaultImplementation_ReachesTheDefaultBody()
    {
        var compilation = CompilationFactory.Create(DefaultImplementationSource);
        var manifest = Manifest("M:Production.DefaultImplementationConsumer.Use(Production.IPinger)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IPinger", "Ping"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IPinger", "Pong"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Pinger", "Pong"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Pinger", "Track"))).IsTrue();
        }
    }
#endif

    [Test]
    public async Task Compute_ConstructedGenericInterface_ReachesTheMatchingImplementation()
    {
        var compilation = CompilationFactory.Create(GenericSource);
        var manifest = Manifest("M:Production.GenericConsumer.UseRepository(Production.IRepository{System.String})");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.IRepository`1", "Get"))).IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.StringRepository", "Get")))
                .IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.StringRepository", "Load")))
                .IsTrue();
        }
    }

    [Test]
    public async Task Compute_ConstructedGenericInterface_DoesNotReachAnotherConstruction()
    {
        var compilation = CompilationFactory.Create(GenericSource);
        var manifest = Manifest("M:Production.GenericConsumer.UseRepository(Production.IRepository{System.String})");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.Int32Repository", "Get")))
                .IsFalse();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.Int32Repository", "Count")))
                .IsFalse();
        }
    }

    [Test]
    public async Task Compute_ConstructedGenericMethod_ReachesTheOverrideOfItsOriginalDefinition()
    {
        var compilation = CompilationFactory.Create(GenericSource);
        var manifest = Manifest("M:Production.GenericConsumer.UseMapper(Production.MapperBase)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.MapperBase", "Map"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Mapper", "Map"))).IsTrue();
            _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Mapper", "Create"))).IsTrue();
        }
    }

    [Test]
    public async Task Compute_ConstructorWithABaseInitializer_ReachesTheBaseConstructorAndItsArgument()
    {
        var compilation = CompilationFactory.Create(BaseConstructorSource);
        var manifest = Manifest("M:Production.Report.#ctor");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Member(compilation, "Production.DocumentBase", ".ctor"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Member(compilation, "Production.Report", "Compose"))).IsTrue();
        _ = await Assert.That(reachable.Contains(Member(compilation, "Production.DocumentBase", "Title"))).IsTrue();
    }

    [Test]
    public async Task Compute_ConstructorWithABaseInitializer_DoesNotReachTheVirtualMemberOfTheBase()
    {
        var compilation = CompilationFactory.Create(BaseConstructorSource);
        var manifest = Manifest("M:Production.Report.#ctor");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        _ = await Assert.That(reachable.Contains(Member(compilation, "Production.DocumentBase", "Render"))).IsFalse();
    }

    /// <summary>
    /// An array type has no containing assembly at all, so the membership test has to survive a
    /// <see langword="null" /> before it compares assemblies.
    /// </summary>
    [Test]
    public async Task Compute_ReferenceToASymbolWithoutAContainingAssembly_IsIgnored()
    {
        var compilation = CompilationFactory.Create(TypeReferenceSource);
        var manifest = Manifest("M:Production.TypeReferences.ArrayType");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.TypeReferences", "ArrayType")))
                .IsTrue();
            _ = await Assert.That(reachable.Count).IsEqualTo(1);
        }
    }

    /// <summary>
    /// The implementation of the interface member lives in a base class that is only available as
    /// metadata, so it has no declaring syntax. It must be skipped silently instead of being walked.
    /// </summary>
    [Test]
    public async Task Compute_InterfaceImplementationFromAReferencedAssembly_IsSkipped()
    {
        var compilation = CreateExternalConsumerCompilation();
        var manifest = Manifest("M:Production.ExternalConsumer.Use(External.IExternalWorker)");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ExternalConsumer", "Use")))
                .IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "External.ExternalWorkerBase", "Work")))
                .IsFalse();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "External.IExternalWorker", "Work")))
                .IsFalse();
        }
    }

    /// <summary>
    /// The closure documents that dispatch is approximated "for every reachable virtual, abstract or
    /// interface member". A member a test touches directly is the most common way for an interface
    /// member to become reachable, so a seeded interface member must reach its implementations exactly
    /// like an interface member that a reachable body calls.
    /// </summary>
    [Test]
    public async Task Compute_SeededInterfaceMember_AlsoReachesItsImplementations()
    {
        var compilation = CompilationFactory.Create(InterfaceSource);
        var manifest = Manifest("M:Production.IHandler.Handle");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.ImplicitHandler", "Handle")))
                .IsTrue();
            _ = await Assert.That(reachable.Contains(ExplicitImplementation(compilation, "Handle"))).IsTrue();
        }
    }

    /// <summary>
    /// The counterpart of <see cref="Compute_SeededInterfaceMember_AlsoReachesItsImplementations" /> for
    /// a virtual method: a test calling the base declaration can end up in every override of it.
    /// </summary>
    [Test]
    public async Task Compute_SeededVirtualMethod_AlsoReachesItsOverride()
    {
        var compilation = CompilationFactory.Create(VirtualSource);
        var manifest = Manifest("M:Production.Renderer.Render");

        var reachable = ReachabilityClosure.Compute(compilation, manifest, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.HtmlRenderer", "Render")))
                .IsTrue();
            _ = await Assert
                .That(reachable.Contains(Member(compilation, "Production.HtmlRenderer", "Decorate")))
                .IsTrue();
        }
    }

    private static CSharpCompilation CreateExternalConsumerCompilation() =>
        CompilationFactory.Create(
            ExternalConsumerSource,
            additionalReferences: [CreateExternalReference()],
            filePath: "ExternalConsumer.cs"
        );

    private static PortableExecutableReference CreateExternalReference()
    {
        var compilation = CompilationFactory.Create(ExternalSource, ExternalAssemblyName, filePath: "External.cs");

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"The fixture assembly '{ExternalAssemblyName}' could not be emitted: "
                    + DiagnosticAssertions.Describe(CompilationFactory.GetCompileErrors(compilation))
            );
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>
    /// One test enters the chain at the base declaration and one enters it at the middle override. The
    /// leaf override is a dispatch target of both, so it belongs to both tests, while the base
    /// declaration keeps the one test that can actually reach it.
    /// </summary>
    [Test]
    public async Task ComputeFromReferences_ChainEnteredAtTwoLevels_UnionsTheAttributionOfTheSharedOverride()
    {
        var compilation = CompilationFactory.Create(OverrideChainSource);
        var references = References(
            (BaseTestId, ["M:Production.LayerConsumer.UseBase(Production.LayerBase)"]),
            (MiddleTestId, ["M:Production.LayerConsumer.UseMiddle(Production.MiddleLayer)"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.LeafLayer", "Describe")))
                .IsEqualTo($"{BaseTestId}, {MiddleTestId}");
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.MiddleLayer", "Describe")))
                .IsEqualTo($"{BaseTestId}, {MiddleTestId}");
        }
    }

    /// <summary>
    /// The counterpart of the union: the base declaration is above the entry point of the second test,
    /// so attributing it to that test would invent a path that does not exist.
    /// </summary>
    [Test]
    public async Task ComputeFromReferences_ChainEnteredAtTwoLevels_KeepsTheBaseDeclarationWithItsOwnTest()
    {
        var compilation = CompilationFactory.Create(OverrideChainSource);
        var references = References(
            (BaseTestId, ["M:Production.LayerConsumer.UseBase(Production.LayerBase)"]),
            (MiddleTestId, ["M:Production.LayerConsumer.UseMiddle(Production.MiddleLayer)"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.LayerBase", "Describe")))
                .IsEqualTo(BaseTestId);
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.LeafLayer", "Detail")))
                .IsEqualTo($"{BaseTestId}, {MiddleTestId}");
        }
    }

    /// <summary>
    /// The dispatch expansion of a seeded abstraction has to carry the attribution of the seed, or every
    /// member behind an interface a test calls directly would look reachable without a single test.
    /// </summary>
    [Test]
    public async Task ComputeFromReferences_SeededInterfaceMember_AttributesTheImplementationsToTheSeedingTest()
    {
        var compilation = CompilationFactory.Create(InterfaceSource);
        var references = References((BaseTestId, ["M:Production.IHandler.Handle"]));

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.ImplicitHandler", "Handle")))
                .IsEqualTo(BaseTestId);
            _ = await Assert
                .That(Describe(reachable, ExplicitImplementation(compilation, "Handle")))
                .IsEqualTo(BaseTestId);
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.ImplicitHandler", "Log")))
                .IsEqualTo(BaseTestId);
        }
    }

    [Test]
    public async Task ComputeFromReferences_AbstractMemberReachedByTwoTests_AttributesBothImplementationsToBoth()
    {
        var compilation = CompilationFactory.Create(AbstractSource);
        var references = References(
            (BaseTestId, ["M:Production.AbstractConsumer.UseValidator(Production.ValidatorBase)"]),
            (MiddleTestId, ["M:Production.AbstractConsumer.UseValidator(Production.ValidatorBase)"])
        );

        var reachable = ReachabilityClosure.ComputeFromReferences(compilation, references, CancellationToken.None);

        using (Assert.Multiple())
        {
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.AlwaysValid", "Accept")))
                .IsEqualTo($"{BaseTestId}, {MiddleTestId}");
            _ = await Assert
                .That(Describe(reachable, Member(compilation, "Production.NeverValid", "Reject")))
                .IsEqualTo($"{BaseTestId}, {MiddleTestId}");
        }
    }

    private static TestSurfaceManifest Manifest(params string[] referencedMemberIds) =>
        new TestSurfaceManifest([], ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds));

    private static ImmutableDictionary<string, ImmutableHashSet<string>> References(
        params (string TestId, string[] ReferencedMemberIds)[] tests
    )
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);

        foreach (var (testId, referencedMemberIds) in tests)
        {
            builder[testId] = ImmutableHashSet.Create(StringComparer.Ordinal, referencedMemberIds);
        }

        return builder.ToImmutable();
    }

    private static string Describe(ReachableSymbolSet reachable, ISymbol symbol) =>
        reachable.GetTestIds(symbol) is { IsEmpty: false } testIds
            ? string.Join(", ", testIds.OrderBy(testId => testId, StringComparer.Ordinal))
            : "<none>";

    private static ISymbol Member(Compilation compilation, string typeName, string memberName) =>
        compilation.GetTypeByMetadataName(typeName)!.GetMembers(memberName)[0];

    private static ISymbol ExplicitImplementation(Compilation compilation, string interfaceMemberName) =>
        compilation
            .GetTypeByMetadataName("Production.ExplicitHandler")!
            .GetMembers()
            .OfType<IMethodSymbol>()
            .First(method =>
                method.ExplicitInterfaceImplementations.Any(implemented =>
                    string.Equals(implemented.Name, interfaceMemberName, StringComparison.Ordinal)
                )
            );
}
