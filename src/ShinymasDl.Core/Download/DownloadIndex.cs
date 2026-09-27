using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinymasDl.Core.Download;

public sealed class IndexRecord
{
    /// <summary> the <c>?v=</c> the file was fetched with; a change means refetch </summary>
    [JsonPropertyName("v")]
    public string Version { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary> the CDN answered with a 404 or the SPA fallback </summary>
    [JsonPropertyName("missing")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Missing { get; set; }
}

/// <summary> what the raw mirror already holds, so a re-run only fetches new or changed versions </summary>
public sealed class DownloadIndex
{
    private const string FileName = ".shinymas-dl-index.json";

    private readonly ConcurrentDictionary<string, IndexRecord> _records;
    private readonly string _path;
    private readonly object _saveLock = new();
    private int _dirtyCount;

    private DownloadIndex(string path, ConcurrentDictionary<string, IndexRecord> records)
    {
        _path = path;
        _records = records;
    }

    public static DownloadIndex Load(string rawRoot)
    {
        var path = Path.Combine(rawRoot, FileName);
        var records = new ConcurrentDictionary<string, IndexRecord>(StringComparer.Ordinal);

        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, IndexRecord>>(stream) ?? [])
                {
                    records[key] = value;
                }
            }
            catch (JsonException)
            {
                // A truncated index from a killed run only costs one re-verify pass.
            }
        }

        return new DownloadIndex(path, records);
    }

    public bool TryGet(string path, out IndexRecord record) => _records.TryGetValue(path, out record!);

    public void Record(string path, IndexRecord record)
    {
        _records[path] = record;

        // Each save rewrites the whole index (tens of MB at full size), so keep it rare.
        if (Interlocked.Increment(ref _dirtyCount) % 5000 == 0)
        {
            Save();
        }
    }

    public void ForgetMissing()
    {
        foreach (var (key, value) in _records)
        {
            if (value.Missing)
            {
                _records.TryRemove(key, out _);
            }
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var snapshot = new SortedDictionary<string, IndexRecord>(StringComparer.Ordinal);
            foreach (var (key, value) in _records)
            {
                snapshot[key] = value;
            }

            var temporary = _path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, snapshot);
            }

            File.Move(temporary, _path, overwrite: true);
        }
    }
}
