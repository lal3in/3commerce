namespace ThreeCommerce.BuildingBlocks.Infrastructure.Storage;

/// <summary>Standard rendition sizes for an uploaded image (mt6_9).</summary>
public enum ImageVariant { Thumbnail = 1, Small = 2, Medium = 3, Large = 4 }

/// <summary>
/// Image variant specs + key derivation (mt6_9). The owning service stores the original; variants are
/// derived keys (<c>name@medium.png</c>) so a renderer/CDN can produce or cache each size. The actual
/// resize is the image-processing step (deferred); this defines the contract.
/// </summary>
public static class ImageVariants
{
    /// <summary>Longest-edge bound, in pixels.</summary>
    public static int MaxDimension(ImageVariant variant) => variant switch
    {
        ImageVariant.Thumbnail => 150,
        ImageVariant.Small => 400,
        ImageVariant.Medium => 800,
        ImageVariant.Large => 1600,
        _ => 800,
    };

    public static string KeyFor(string originalKey, ImageVariant variant)
    {
        var dot = originalKey.LastIndexOf('.');
        var stem = dot < 0 ? originalKey : originalKey[..dot];
        var extension = dot < 0 ? string.Empty : originalKey[dot..];
        return $"{stem}@{variant.ToString().ToLowerInvariant()}{extension}";
    }
}
