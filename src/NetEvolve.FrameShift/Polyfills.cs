namespace System.Runtime.CompilerServices;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

#if !NET5_0_OR_GREATER
/// <summary>
/// Marker type the compiler requires to emit <see langword="init" /> accessors on target frameworks
/// that do not provide it themselves.
/// </summary>
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[SuppressMessage(
    "Design",
    "MA0048:File name must match type name",
    Justification = "All compiler polyfills are intentionally grouped in a single file, they are not part of the API."
)]
internal static class IsExternalInit { }
#endif

#if !NET7_0_OR_GREATER
/// <summary>
/// Marker attribute the compiler emits on members declared with the <see langword="required" /> modifier
/// on target frameworks that do not provide it themselves.
/// </summary>
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false
)]
[SuppressMessage(
    "Design",
    "MA0048:File name must match type name",
    Justification = "All compiler polyfills are intentionally grouped in a single file, they are not part of the API."
)]
internal sealed class RequiredMemberAttribute : Attribute { }

/// <summary>
/// Marker attribute the compiler emits to signal that a compiler feature is required to consume
/// the annotated element, on target frameworks that do not provide it themselves.
/// </summary>
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
[SuppressMessage(
    "Design",
    "MA0048:File name must match type name",
    Justification = "All compiler polyfills are intentionally grouped in a single file, they are not part of the API."
)]
internal sealed class CompilerFeatureRequiredAttribute : Attribute
{
    /// <summary>
    /// The <see cref="FeatureName" /> used for the <see langword="ref" /> <see langword="struct" /> feature.
    /// </summary>
    public const string RefStructs = "RefStructs";

    /// <summary>
    /// The <see cref="FeatureName" /> used for the <see langword="required" /> members feature.
    /// </summary>
    public const string RequiredMembers = "RequiredMembers";

    /// <summary>
    /// Initializes a new instance of the <see cref="CompilerFeatureRequiredAttribute" /> class.
    /// </summary>
    /// <param name="featureName">The name of the required compiler feature.</param>
    public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

    /// <summary>
    /// Gets the name of the compiler feature required to consume the annotated element.
    /// </summary>
    public string FeatureName { get; }

    /// <summary>
    /// Gets a value indicating whether the compiler may silently ignore an unknown feature name.
    /// </summary>
    public bool IsOptional { get; set; }
}
#endif
