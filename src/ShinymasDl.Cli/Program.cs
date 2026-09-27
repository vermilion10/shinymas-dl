using System.CommandLine;
using ShinymasDl.Core;
using ShinymasDl.Core.Catalog;
using ShinymasDl.Core.Download;

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

    private static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KiB",
        _ => $"{bytes} B",
    };
}
