using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShinymasDl.Core.Api;

/// <summary> one character's album: asset ids mapped to the 32-character prefix the CDN expects </summary>
public sealed class CharacterHashes
{
    [JsonPropertyName("cards")]
    public SortedDictionary<string, string> Cards { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("costumes")]
    public SortedDictionary<string, string> Costumes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("evolutionSkins")]
    public SortedDictionary<string, string> EvolutionSkins { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("whispers")]
    public SortedDictionary<string, string> Whispers { get; set; } = new(StringComparer.Ordinal);
}

/// <summary> <c>data/card-hashes.json</c>. A character is present only once its album was fully read, which is what makes a crawl resumable </summary>
public sealed class CardHashes
{
    public const string FileName = "card-hashes.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [JsonPropertyName("characters")]
    public SortedDictionary<string, CharacterHashes> Characters { get; set; } = new(Comparer<string>.Create(CompareIds));

    public static string PathIn(string dataRoot) => Path.Combine(dataRoot, FileName);

    public static CardHashes Load(string dataRoot)
    {
        var path = PathIn(dataRoot);
        if (!File.Exists(path))
        {
            return new CardHashes();
        }

        var loaded = JsonSerializer.Deserialize<CardHashes>(File.ReadAllText(path)) ?? new CardHashes();
        var store = new CardHashes();
        foreach (var (id, hashes) in loaded.Characters)
        {
            store.Characters[id] = hashes;
        }

        return store;
    }

    public void Save(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = PathIn(dataRoot);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyDictionary<string, string> AllCards() => Flatten(c => c.Cards);

    public IReadOnlyDictionary<string, string> AllWhispers() => Flatten(c => c.Whispers);

    private Dictionary<string, string> Flatten(Func<CharacterHashes, SortedDictionary<string, string>> select)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var character in Characters.Values)
        {
            foreach (var (id, hash) in select(character))
            {
                result[id] = hash;
            }
        }

        return result;
    }

    private static int CompareIds(string? a, string? b) =>
        int.TryParse(a, out var x) && int.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);
}
