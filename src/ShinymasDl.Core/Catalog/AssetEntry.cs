using ShinymasDl.Core.Crypto;

namespace ShinymasDl.Core.Catalog;

/// <summary>
/// One file from the asset map: its logical path and the version the CDN expects in <c>?v=</c>.
/// <see cref="CdnPath"/> is set only when a card hash is known and the file lives at <c>&lt;hash&gt;_&lt;id&gt;</c> instead.
/// </summary>
public sealed record AssetEntry(string Path, string Version, string? CdnPath = null)
{
    /// <summary> top-level folder: images, sounds, spine, json, movies, ae, particles, fonts </summary>
    public string Category => Path[..Math.Max(0, Path.IndexOf('/'))];

    public string Url(string assetRoot) => UrlFor(assetRoot, CdnPath ?? Path);

    public string PlainUrl(string assetRoot) => UrlFor(assetRoot, Path);

    /// <summary> every .png has a .webp twin at its own hash; the asset map lists only the .png </summary>
    public AssetEntry AsWebP() =>
        System.IO.Path.GetExtension(Path) == ".png"
            ? this with
            {
                Path = System.IO.Path.ChangeExtension(Path, ".webp"),
                CdnPath = CdnPath is null ? null : System.IO.Path.ChangeExtension(CdnPath, ".webp"),
            }
            : this;

    private string UrlFor(string assetRoot, string path) => $"{assetRoot}{AssetCrypto.HashName(path)}?v={Version}";
}
