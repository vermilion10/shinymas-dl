using ShinymasDl.Core.Crypto;

namespace ShinymasDl.Core.Catalog;

/// <summary> one file from the asset map: its logical path and the version the CDN expects in <c>?v=</c> </summary>
public sealed record AssetEntry(string Path, string Version)
{
    /// <summary> top-level folder: images, sounds, spine, json, movies, ae, particles, fonts </summary>
    public string Category => Path[..Math.Max(0, Path.IndexOf('/'))];

    public string Url(string assetRoot) => $"{assetRoot}{AssetCrypto.HashName(Path)}?v={Version}";

    /// <summary> every .png has a .webp twin at its own hash; the asset map lists only the .png </summary>
    public AssetEntry AsWebP() =>
        System.IO.Path.GetExtension(Path) == ".png" ? this with { Path = System.IO.Path.ChangeExtension(Path, ".webp") } : this;
}
