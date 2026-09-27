namespace ShinymasDl.Core.Catalog;

/// <summary> selects entries by top-level category and by case-insensitive substrings, all of which must match </summary>
public sealed class AssetFilter(IReadOnlySet<string>? categories, IReadOnlyList<string> terms)
{
    public static readonly string[] Categories = ["images", "sounds", "spine", "json", "movies", "ae", "particles", "fonts"];

    public bool Matches(AssetEntry entry) =>
        (categories is null || categories.Contains(entry.Category))
        && terms.All(term => entry.Path.Contains(term, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AssetEntry> Apply(IEnumerable<AssetEntry> entries) => entries.Where(Matches);

    /// <summary> parses <c>--categories</c>; "all" or empty means no restriction </summary>
    public static IReadOnlySet<string>? ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var category = name.ToLowerInvariant();
            if (!Categories.Contains(category))
            {
                throw new InvalidOperationException(
                    $"Unknown category '{name}'. Valid: {string.Join(", ", Categories)}");
            }

            result.Add(category);
        }

        return result;
    }
}
