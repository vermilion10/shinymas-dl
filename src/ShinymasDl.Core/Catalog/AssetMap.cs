using System.Text.Json;
using System.Text.Json.Nodes;
using ShinymasDl.Core.Crypto;
using ShinymasDl.Core.Download;

namespace ShinymasDl.Core.Catalog;

/// <summary> the client's own file list: a manifest naming chunks, each mapping about 3000 paths to a version </summary>
public sealed class AssetMap
{
    private const string ManifestFile = "asset-map.json";
    private const string ChunkDirectory = "asset-map";

    private AssetMap(int version, long totalSize, IReadOnlyList<AssetEntry> entries)
    {
        Version = version;
        TotalSize = totalSize;
        Entries = entries;
    }

    public int Version { get; }

    /// <summary> as reported by the manifest, pngs rather than webps </summary>
    public long TotalSize { get; }

    public IReadOnlyList<AssetEntry> Entries { get; }

    public static bool Exists(string dataRoot) => File.Exists(System.IO.Path.Combine(dataRoot, ManifestFile));

    public static AssetMap Load(string dataRoot)
    {
        var manifestPath = System.IO.Path.Combine(dataRoot, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"No asset map in {System.IO.Path.GetFullPath(dataRoot)}. Run 'shinymas-dl refresh' first.");
        }

        var manifest = Manifest.Parse(File.ReadAllText(manifestPath));
        var entries = new List<AssetEntry>(manifest.Chunks.Count * 3000);

        foreach (var chunk in manifest.Chunks.Keys)
        {
            var chunkPath = System.IO.Path.Combine(dataRoot, ChunkDirectory, chunk);
            if (!File.Exists(chunkPath))
            {
                throw new InvalidOperationException($"Asset map chunk {chunk} is missing. Run 'shinymas-dl refresh' again.");
            }

            foreach (var (path, version) in ParseVersions(File.ReadAllText(chunkPath)))
            {
                entries.Add(new AssetEntry(path, version));
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new AssetMap(manifest.Version, manifest.TotalSize, entries);
    }

    /// <summary> fetches the manifest and any chunk whose version moved since the last refresh </summary>
    public static async Task<RefreshResult> RefreshAsync(
        string dataRoot, string assetRoot, Action<string> log, CancellationToken cancellationToken)
    {
        var manifestPath = System.IO.Path.Combine(dataRoot, ManifestFile);
        var chunkRoot = System.IO.Path.Combine(dataRoot, ChunkDirectory);
        Directory.CreateDirectory(chunkRoot);

        var previous = File.Exists(manifestPath) ? Manifest.Parse(File.ReadAllText(manifestPath)) : null;

        // The manifest is fetched without ?v=; CloudFront keeps stale copies under old query strings.
        var manifestUrl = $"{assetRoot}asset-map-{AssetCrypto.HashName(ManifestFile)}";
        var manifestText = await FetchTextAsync(manifestUrl, cancellationToken);
        var manifest = Manifest.Parse(manifestText);

        var fetched = 0;
        foreach (var (chunk, version) in manifest.Chunks)
        {
            var chunkPath = System.IO.Path.Combine(chunkRoot, chunk);
            if (previous?.Chunks.GetValueOrDefault(chunk) == version && File.Exists(chunkPath))
            {
                continue;
            }

            var text = await FetchTextAsync($"{assetRoot}{AssetCrypto.HashName(chunk)}?v={version}", cancellationToken);
            await File.WriteAllTextAsync(chunkPath, text, cancellationToken);
            fetched++;
            log($"  {chunk} (v{version})");
        }

        // Written last so an interrupted refresh redoes the chunks it did not finish.
        await File.WriteAllTextAsync(manifestPath, manifestText, cancellationToken);

        return new RefreshResult(previous?.Version, manifest.Version, manifest.Chunks.Count, fetched);
    }

    private static async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await ShinyHttpClient.Instance.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (ShinyHttpClient.IsSpaFallback(response))
        {
            throw new InvalidOperationException($"The CDN has no file at {url}; the hashing scheme may have changed.");
        }

        return AssetCrypto.DecryptText(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseVersions(string json)
    {
        foreach (var (path, node) in JsonNode.Parse(json)!.AsObject())
        {
            yield return new KeyValuePair<string, string>(path, node!.ToJsonString().Trim('"'));
        }
    }

    public sealed record RefreshResult(int? PreviousVersion, int Version, int Chunks, int FetchedChunks);

    private sealed record Manifest(int Version, long TotalSize, IReadOnlyDictionary<string, string> Chunks)
    {
        public static Manifest Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // chunks is an array of single-key objects: [{"asset-map-chunk-0.json": 77}, ...]
            var chunks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chunk in root.GetProperty("chunks").EnumerateArray())
            {
                foreach (var property in chunk.EnumerateObject())
                {
                    chunks[property.Name] = property.Value.ToString();
                }
            }

            return new Manifest(
                root.GetProperty("version").GetInt32(),
                root.TryGetProperty("totalSize", out var size) ? size.GetInt64() : 0,
                chunks);
        }
    }
}
