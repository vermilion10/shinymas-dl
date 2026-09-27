using System.CommandLine;
using ShinymasDl.Core;
using ShinymasDl.Core.Catalog;
using ShinymasDl.Core.Download;
using ShinymasDl.Core.Extraction;
using ShinymasDl.Core.Naming;

namespace ShinymasDl.Cli;

/// <summary> targets System.CommandLine 3.0.0-preview.7: SetAction, Options.Add, and Parse(args).InvokeAsync() </summary>
public static class Program
{
    private static readonly Option<string> DataOption = new("--data")
    {
        Description = "Directory holding the cached asset map",
        DefaultValueFactory = _ => "data",
        Recursive = true,
    };

    private static readonly Option<string> OutputOption = new("--output")
    {
        Description = "Output directory; the raw mirror lives in <output>/raw",
        DefaultValueFactory = _ => "output",
        Recursive = true,
    };

    private static readonly Option<string> AssetRootOption = new("--asset-root")
    {
        Description = "CDN asset root URL",
        DefaultValueFactory = _ => ShinyHttpClient.DefaultAssetRoot,
        Recursive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand("Download and organize THE IDOLM@STER SHINY COLORS assets from the game CDN.");
        root.Options.Add(DataOption);
        root.Options.Add(OutputOption);
        root.Options.Add(AssetRootOption);

        root.Subcommands.Add(BuildRefreshCommand());
        root.Subcommands.Add(BuildListCommand());
        root.Subcommands.Add(BuildDownloadCommand());
        root.Subcommands.Add(BuildNamesCommand());
        root.Subcommands.Add(BuildExtractCommand());

        return await root.Parse(args).InvokeAsync();
    }

    private static Workspace WorkspaceFrom(ParseResult parseResult) =>
        new(parseResult.GetValue(DataOption)!, parseResult.GetValue(OutputOption)!);

    private static string AssetRootFrom(ParseResult parseResult)
    {
        var value = parseResult.GetValue(AssetRootOption)!;
        return value.EndsWith('/') ? value : value + "/";
    }

    private static Option<string> CategoriesOption() => new("--categories")
    {
        Description = "Comma-separated: " + string.Join(", ", AssetFilter.Categories),
        DefaultValueFactory = _ => "all",
    };

    private static Argument<string[]> FiltersArgument() => new("filters")
    {
        Description = "Case-insensitive substrings; a path must contain all of them",
        Arity = ArgumentArity.ZeroOrMore,
    };

    private static Command BuildRefreshCommand()
    {
        var command = new Command("refresh", "Fetch or update the asset map from the CDN");

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = WorkspaceFrom(parseResult);
            Console.WriteLine($"Refreshing asset map into {Path.GetFullPath(workspace.DataRoot)}");

            var result = await AssetMap.RefreshAsync(
                workspace.DataRoot, AssetRootFrom(parseResult), Console.WriteLine, cancellationToken);

            var change = result.PreviousVersion is { } previous && previous != result.Version
                ? $"v{previous} -> v{result.Version}"
                : $"v{result.Version}";
            Console.WriteLine($"Asset map {change}: fetched {result.FetchedChunks} of {result.Chunks} chunks");

            var map = workspace.LoadAssetMap();
            Console.WriteLine($"{map.Entries.Count} files, {Human(map.TotalSize)} as listed");
            return 0;
        });

        return command;
    }

    private static Command BuildListCommand()
    {
        var filtersArgument = FiltersArgument();
        var categoriesOption = CategoriesOption();
        var statsOption = new Option<bool>("--stats") { Description = "Print counts per folder instead of paths" };
        var depthOption = new Option<int>("--depth")
        {
            Description = "Folder depth for --stats",
            DefaultValueFactory = _ => 3,
        };

        var command = new Command("list", "Print asset paths from the cached asset map");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(categoriesOption);
        command.Options.Add(statsOption);
        command.Options.Add(depthOption);

        command.SetAction(parseResult =>
        {
            var map = WorkspaceFrom(parseResult).LoadAssetMap();
            var filter = new AssetFilter(
                AssetFilter.ParseCategories(parseResult.GetValue(categoriesOption)),
                parseResult.GetValue(filtersArgument) ?? []);
            var entries = filter.Apply(map.Entries).ToList();

            if (parseResult.GetValue(statsOption))
            {
                var depth = Math.Max(1, parseResult.GetValue(depthOption));
                var groups = entries
                    .GroupBy(e => string.Join('/', e.Path.Split('/').SkipLast(1).Take(depth)))
                    .OrderByDescending(g => g.Count());

                foreach (var group in groups)
                {
                    Console.WriteLine($"{group.Count(),8}  {group.Key}/");
                }

                Console.WriteLine($"{entries.Count,8}  total");
                return 0;
            }

            foreach (var entry in entries)
            {
                Console.WriteLine(entry.Path);
            }

            return 0;
        });

        return command;
    }

    private static Command BuildDownloadCommand()
    {
        var filtersArgument = FiltersArgument();
        var categoriesOption = CategoriesOption();
        var webpOption = new Option<bool>("--webp")
        {
            Description = "Fetch the .webp twin of every .png (smaller, lossy); PNG is the default",
        };
        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Simultaneous requests",
            DefaultValueFactory = _ => 8,
        };
        var retryMissingOption = new Option<bool>("--retry-missing")
        {
            Description = "Re-check files previously recorded as missing",
        };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Count what would be fetched without fetching" };

        var command = new Command("download", "Mirror assets from the CDN into <output>/raw");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(categoriesOption);
        command.Options.Add(webpOption);
        command.Options.Add(concurrencyOption);
        command.Options.Add(retryMissingOption);
        command.Options.Add(dryRunOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = WorkspaceFrom(parseResult);
            var map = workspace.LoadAssetMap();
            var filter = new AssetFilter(
                AssetFilter.ParseCategories(parseResult.GetValue(categoriesOption)),
                parseResult.GetValue(filtersArgument) ?? []);

            var entries = filter.Apply(map.Entries).ToList();
            if (parseResult.GetValue(webpOption))
            {
                entries = entries.Select(e => e.AsWebP()).ToList();
            }

            Console.WriteLine($"Asset map v{map.Version}: {entries.Count} of {map.Entries.Count} files selected");

            var options = new DownloadOptions(
                AssetRootFrom(parseResult),
                workspace.RawRoot,
                parseResult.GetValue(concurrencyOption),
                parseResult.GetValue(retryMissingOption),
                parseResult.GetValue(dryRunOption));

            var index = DownloadIndex.Load(workspace.RawRoot);
            var progress = new ConsoleProgress(entries.Count);
            var stats = await new Downloader(options, index, progress.Log).RunAsync(entries, progress, cancellationToken);
            progress.Finish();

            var verb = options.DryRun ? "Would fetch" : "Downloaded";
            Console.WriteLine(
                $"{verb} {stats.Downloaded} ({Human(stats.Bytes)}), up to date {stats.Skipped}, " +
                $"missing {stats.Missing}, failed {stats.Failed}");

            return stats.Failed == 0 ? 0 : 1;
        });

        return command;
    }

    /// <summary> merges names from the mirrored scripts into names.json, so a partial mirror never shrinks it </summary>
    private static NameIndex ResolveNames(Workspace workspace)
    {
        var cached = NameIndex.Load(workspace.DataRoot);
        var built = NameIndex.Build(workspace.RawRoot);
        if (built.Count == 0)
        {
            return cached;
        }

        var merged = cached.MergedWith(built);
        merged.Save(workspace.DataRoot);
        return merged;
    }

    private static Command BuildNamesCommand()
    {
        var command = new Command("names", "Build character names from the mirrored story scripts and print them");

        command.SetAction(parseResult =>
        {
            var workspace = WorkspaceFrom(parseResult);
            var names = ResolveNames(workspace);

            if (names.Count == 0)
            {
                Console.Error.WriteLine("No story scripts in the raw mirror. Run 'shinymas-dl download --categories json' first.");
                return 1;
            }

            foreach (var (id, name) in names.Characters.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{id}  {name.Label,-12}  {name.Name}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildExtractCommand()
    {
        var filtersArgument = FiltersArgument();
        var categoriesOption = CategoriesOption();
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Rewrite output files that already exist" };
        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Files processed in parallel",
            DefaultValueFactory = _ => Environment.ProcessorCount,
        };

        var command = new Command("extract", "Decrypt and organize the raw mirror into the named output tree");
        command.Arguments.Add(filtersArgument);
        command.Options.Add(categoriesOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(concurrencyOption);

        command.SetAction(parseResult =>
        {
            var workspace = WorkspaceFrom(parseResult);
            if (!Directory.Exists(workspace.RawRoot))
            {
                Console.Error.WriteLine($"No raw mirror at {Path.GetFullPath(workspace.RawRoot)}. Run 'shinymas-dl download' first.");
                return 1;
            }

            var names = ResolveNames(workspace);
            Console.WriteLine(names.Count > 0
                ? $"Named {names.Count} characters from the story scripts"
                : "No story scripts mirrored; character folders will use bare ids");

            var filter = new AssetFilter(
                AssetFilter.ParseCategories(parseResult.GetValue(categoriesOption)),
                parseResult.GetValue(filtersArgument) ?? []);
            var options = new ExtractOptions(
                workspace.RawRoot,
                workspace.OutputRoot,
                parseResult.GetValue(overwriteOption),
                parseResult.GetValue(concurrencyOption));

            var stats = new Extractor(options, names, Console.Error.WriteLine).Run(filter);

            Console.WriteLine(
                $"Wrote {stats.Written} files ({stats.Scripts} transcripts), skipped {stats.Skipped} existing, " +
                $"failed {stats.Failed} into {Path.GetFullPath(workspace.OutputRoot)}");

            return stats.Failed == 0 ? 0 : 1;
        });

        return command;
    }

    private static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };

    /// <summary> one rewritten status line; per-file output would bury the failures </summary>
    private sealed class ConsoleProgress(int total) : IProgress<DownloadStats>
    {
        private readonly object _lock = new();
        private DateTime _lastDraw = DateTime.MinValue;

        public void Report(DownloadStats value)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (Console.IsOutputRedirected || (value.Done < total && now - _lastDraw < TimeSpan.FromMilliseconds(250)))
                {
                    return;
                }

                _lastDraw = now;
                Console.Write(
                    $"\r  {value.Done}/{total}  got {value.Downloaded} ({Human(value.Bytes)})  " +
                    $"cached {value.Skipped}  missing {value.Missing}  failed {value.Failed}   ");
            }
        }

        public void Log(string message)
        {
            lock (_lock)
            {
                Console.WriteLine();
                Console.Error.WriteLine(message);
            }
        }

        public void Finish() => Console.WriteLine();
    }
}
