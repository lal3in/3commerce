using Microsoft.Extensions.Localization;

// The assembly is named "3commerce.Admin" but its C# root namespace is "ThreeCommerce.Admin"
// (a C# namespace cannot start with a digit — see Directory.Build.props). ResourceManagerStringLocalizerFactory
// derives the .resx base name from the ASSEMBLY name unless this attribute tells it otherwise, so without it the
// localizer would look for "3commerce.Admin.Resources.ThreeCommerce.Admin.SharedResource" and find nothing.
[assembly: RootNamespace("ThreeCommerce.Admin")]

namespace ThreeCommerce.Admin;

/// <summary>
/// Marker type for the admin's shared string resources: <c>Resources/SharedResource.resx</c> (English base,
/// the neutral fallback) plus one <c>Resources/SharedResource.&lt;culture&gt;.resx</c> per translated culture.
/// Inject <see cref="IStringLocalizer{SharedResource}"/> (aliased as <c>L</c> in <c>_Imports.razor</c>) and look
/// strings up by key: <c>L["Nav.Catalog"]</c>. A key with no entry in the active culture falls back to the
/// English base automatically, so a partially translated culture is always safe to ship.
/// </summary>
public sealed class SharedResource
{
}
