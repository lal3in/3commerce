using Microsoft.Extensions.Localization;

// The assembly is named "3commerce.SupplierPortal" but its namespaces are "ThreeCommerce.*" (a C#
// identifier cannot start with a digit — see Directory.Build.props). The localizer derives the
// resource base name from the assembly name unless this attribute says otherwise, so without it
// every lookup misses and the portal renders resource keys instead of text.
[assembly: RootNamespace("ThreeCommerce.SupplierPortal")]

namespace ThreeCommerce.SupplierPortal;

/// <summary>
/// Marker type for the supplier portal's shared string resources.
/// <para>
/// Strings live in <c>Resources/SharedResource.resx</c> (English base, the fallback for every
/// culture) plus one <c>Resources/SharedResource.&lt;culture&gt;.resx</c> per translated culture.
/// </para>
/// <para>
/// Adding a language is a drop-in: copy the base file to
/// <c>Resources/SharedResource.&lt;culture&gt;.resx</c>, translate the values, and add the culture
/// code to <c>Localization:SupportedCultures</c> in appsettings.json. Any key that is missing from
/// a culture file falls back to the English base, so partial translations are safe to ship.
/// </para>
/// <para>
/// This type deliberately sits in the project root namespace: with
/// <c>ResourcesPath = "Resources"</c> the localizer resolves
/// <c>ThreeCommerce.SupplierPortal.SharedResource</c> to <c>Resources/SharedResource.resx</c>.
/// Moving it into the Resources namespace would break that lookup.
/// </para>
/// </summary>
public sealed class SharedResource;
