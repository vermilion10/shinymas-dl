using System.Net;
using ShinymasDl.Core.Catalog;

namespace ShinymasDl.Core.Download;

public sealed record DownloadOptions(
    string AssetRoot,
    string RawRoot,
    int Concurrency = 8,
    bool RetryMissing = false,
    bool DryRun = false);

public sealed record DownloadStats(int Downloaded, int Skipped, int Missing, int Failed, long Bytes)
{
    public int Done => Downloaded + Skipped + Missing + Failed;
}

/// <summary> mirrors entries into the raw tree under their logical paths; text assets stay encrypted there </summary>
public sealed class Downloader(DownloadOptions options, DownloadIndex index, Action<string> log)
{
    private int _downloaded;
    private int _skipped;
    private int _missing;
    private int _failed;
    private long _bytes;

    public async Task<DownloadStats> RunAsync(
        IReadOnlyList<AssetEntry> entries, IProgress<DownloadStats>? progress, CancellationToken cancellationToken)
    {
        if (options.RetryMissing)
        {
            index.ForgetMissing();
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken,
        };

        try
        {
            await Parallel.ForEachAsync(entries, parallelOptions, async (entry, token) =>
            {
                switch (await FetchAsync(entry, token))
                {
                    case Outcome.Downloaded: Interlocked.Increment(ref _downloaded); break;
                    case Outcome.Skipped: Interlocked.Increment(ref _skipped); break;
                    case Outcome.Missing: Interlocked.Increment(ref _missing); break;
                    default: Interlocked.Increment(ref _failed); break;
                }

                progress?.Report(Snapshot());
            });
        }
        finally
        {
            if (!options.DryRun)
            {
                index.Save();
            }
        }

        return Snapshot();
    }

    private DownloadStats Snapshot() => new(
        Volatile.Read(ref _downloaded),
        Volatile.Read(ref _skipped),
        Volatile.Read(ref _missing),
        Volatile.Read(ref _failed),
        Interlocked.Read(ref _bytes));

    private enum Outcome
    {
        Downloaded,
        Skipped,
        Missing,
        Failed,
    }

    private async Task<Outcome> FetchAsync(AssetEntry entry, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(options.RawRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));

        if (index.TryGet(entry.Path, out var record) && record.Version == entry.Version)
        {
            // A file recorded missing before its card hash was known gets one more try with the hash.
            if (record.Missing && (entry.CdnPath is null || record.Hashed))
            {
                return Outcome.Missing;
            }

            if (File.Exists(destination) && new FileInfo(destination).Length == record.Size)
            {
                return Outcome.Skipped;
            }
        }

        if (options.DryRun)
        {
            return Outcome.Downloaded;
        }

        var hashed = entry.CdnPath is not null;
        string[] urls = hashed ? [entry.Url(options.AssetRoot), entry.PlainUrl(options.AssetRoot)] : [entry.Url(options.AssetRoot)];

        try
        {
            foreach (var url in urls)
            {
                using var response = await ShinyHttpClient.Instance.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                    || (response.IsSuccessStatusCode && ShinyHttpClient.IsSpaFallback(response)))
                {
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    log($"  {(int)response.StatusCode} {entry.Path}");
                    return Outcome.Failed;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var temporary = destination + ".part";
                long size;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = File.Create(temporary))
                {
                    await source.CopyToAsync(target, cancellationToken);
                    size = target.Length;
                }

                File.Move(temporary, destination, overwrite: true);
                index.Record(entry.Path, new IndexRecord { Version = entry.Version, Size = size, Hashed = hashed });
                Interlocked.Add(ref _bytes, size);
                return Outcome.Downloaded;
            }

            index.Record(entry.Path, new IndexRecord { Version = entry.Version, Missing = true, Hashed = hashed });
            return Outcome.Missing;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            log($"  error {entry.Path}: {ex.Message}");
            return Outcome.Failed;
        }
    }
}
