using ShinymasDl.Core.Catalog;

namespace ShinymasDl.Core;

/// <summary> the on-disk layout: <c>data/</c> holds the asset map, <c>output/raw/</c> the mirror, and the organized tree sits beside it </summary>
public sealed class Workspace(string dataDirectory, string outputDirectory)
{
    public string DataRoot { get; } = dataDirectory;

    public string OutputRoot { get; } = outputDirectory;

    public string RawRoot { get; } = Path.Combine(outputDirectory, "raw");

    public AssetMap LoadAssetMap() => AssetMap.Load(DataRoot);

    public string RawPath(string assetPath) => Path.Combine(RawRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
}
