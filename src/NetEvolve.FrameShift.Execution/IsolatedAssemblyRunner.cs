namespace NetEvolve.FrameShift.Execution;

using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Loads a mutant assembly image into its own collectible <see cref="AssemblyLoadContext" /> and invokes
/// exactly one method of it by reflection, so that a mutant is executed as real code instead of only
/// being reasoned about statically.
/// </summary>
/// <remarks>
/// <para>
/// The method convention is deliberately the narrowest one that still matches how every mainstream
/// assertion library actually signals a failure: a public, parameterless instance or static method that
/// returns normally, or returns a <see cref="Task" /> that completes normally, on success, and throws -
/// directly or by faulting the returned task - on failure. xUnit, NUnit, MSTest and TUnit assertions all
/// throw on a failed assertion, so a plain reflection invocation of the test method body already observes
/// the same signal a real test host would; what is not reproduced here is test discovery, fixtures, data
/// rows, and every other concern the frameworks themselves own.
/// </para>
/// <para>
/// Every call gets a fresh, uniquely named context, and the context is asked to unload once the
/// invocation returns. Unloading only completes once every reference into the context is collected by
/// the garbage collector, which happens on its own schedule; nothing here forces that collection, since
/// nothing here depends on the memory being reclaimed synchronously.
/// </para>
/// </remarks>
internal static class IsolatedAssemblyRunner
{
    /// <summary>
    /// Loads <paramref name="assemblyBytes" /> into a fresh, collectible <see cref="AssemblyLoadContext" />
    /// and invokes the named method of the named type.
    /// </summary>
    /// <param name="assemblyBytes">The assembly image to load, as produced by <see cref="MutantAssemblyBuilder" />.</param>
    /// <param name="typeFullName">The full name of the type declaring the method to invoke.</param>
    /// <param name="methodName">The name of the parameterless method to invoke.</param>
    /// <returns>Whether the invocation passed or failed, and the exception of a failure.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="assemblyBytes" />, <paramref name="typeFullName" /> or <paramref name="methodName" />
    /// is <see langword="null" />.
    /// </exception>
    public static TestExecutionResult InvokeParameterlessTest(
        byte[] assemblyBytes,
        string typeFullName,
        string methodName
    )
    {
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentNullException.ThrowIfNull(typeFullName);
        ArgumentNullException.ThrowIfNull(methodName);

        var context = new AssemblyLoadContext(
            "NetEvolve.FrameShift.Execution.Mutant." + Guid.NewGuid().ToString("N"),
            isCollectible: true
        );

        try
        {
            return Invoke(context, assemblyBytes, typeFullName, methodName);
        }
        finally
        {
            context.Unload();
        }
    }

    private static TestExecutionResult Invoke(
        AssemblyLoadContext context,
        byte[] assemblyBytes,
        string typeFullName,
        string methodName
    )
    {
        using var assemblyStream = new MemoryStream(assemblyBytes);
        var assembly = context.LoadFromStream(assemblyStream);

        var type =
            assembly.GetType(typeFullName, throwOnError: false)
            ?? throw new TypeLoadException($"The mutant assembly does not declare a type named '{typeFullName}'.");

        // Public only, deliberately: every mainstream test framework requires a public test method, so
        // reaching into non-public members here would recognise a shape none of them actually run.
        var method =
            type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new MissingMethodException(typeFullName, methodName);

        var instance = method.IsStatic ? null : Activator.CreateInstance(type);

        try
        {
            var result = method.Invoke(instance, parameters: null);

            if (result is Task task)
            {
                // Deliberately synchronous: this whole call chain, up to and including the caller that
                // requested one test method's pass/fail verdict, is synchronous by design for this stage
                // of execution-based verification, and a mutant assembly's collectible
                // AssemblyLoadContext must not be asked to unload while an async continuation could still
                // be running on it.
#pragma warning disable VSTHRD002 // Synchronously waiting on tasks or awaiters may cause deadlocks
                task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }

            return TestExecutionResult.Passed();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return TestExecutionResult.Failed(exception.InnerException);
        }
        catch (Exception exception)
        {
            return TestExecutionResult.Failed(exception);
        }
    }
}
