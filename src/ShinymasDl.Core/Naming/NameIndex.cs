using System.Text.Json;
using System.Text.Json.Serialization;
using ShinymasDl.Core.Crypto;

namespace ShinymasDl.Core.Naming;

public sealed record CharacterName(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("name")] string? Name);

/// <summary> character names learned from the game's story scripts, the only place the CDN spells them out </summary>
public sealed class NameIndex
{
    private const string FileName = "names.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyDictionary<string, CharacterName> _characters;

    private NameIndex(IReadOnlyDictionary<string, CharacterName> characters) => _characters = characters;

    public static NameIndex Empty { get; } = new(new Dictionary<string, CharacterName>());

    public int Count => _characters.Count;

    public IReadOnlyDictionary<string, CharacterName> Characters => _characters;

    /// <summary> folder name for a 3-digit character id: <c>001_mano</c>, or the bare id when unknown </summary>
    public string CharacterFolder(string characterId) =>
        _characters.TryGetValue(characterId, out var name) ? $"{characterId}_{SafeName.Clean(name.Label)}" : characterId;

    /// <summary> card ids embed the character: 1 04 001 0010 is type, rarity, character, sequence </summary>
    public static string CharacterIdOfCard(string cardId) => cardId.Substring(3, 3);

    public static NameIndex Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, FileName);
        if (!File.Exists(path))
        {
            return Empty;
        }

        var characters = JsonSerializer.Deserialize<SortedDictionary<string, CharacterName>>(File.ReadAllText(path));
        return new NameIndex(characters ?? []);
    }

    public void Save(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var sorted = new SortedDictionary<string, CharacterName>(_characters.ToDictionary(), StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(dataRoot, FileName), JsonSerializer.Serialize(sorted, SerializerOptions));
    }

    /// <summary> scans the encrypted scripts under <c>raw/json</c> for charId, charLabel and speaker </summary>
    public static NameIndex Build(string rawRoot)
    {
        var scriptRoot = Path.Combine(rawRoot, "json");
        if (!Directory.Exists(scriptRoot))
        {
            return Empty;
        }

        var labels = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var speakers = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var gate = new object();

        Parallel.ForEach(Directory.EnumerateFiles(scriptRoot, "*.json", SearchOption.AllDirectories), file =>
        {
            List<(string Id, string Label)> localLabels = [];
            List<(string Label, string Speaker)> localSpeakers = [];

            try
            {
                using var document = JsonDocument.Parse(AssetCrypto.Decrypt(File.ReadAllBytes(file)));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var line in document.RootElement.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.Object || Text(line, "charLabel") is not { } label)
                    {
                        continue;
                    }

                    var id = Text(line, "charId");
                    switch (Text(line, "charType"))
                    {
                        case "characters" when id is { Length: 3 }:
                            localLabels.Add((id, label));
                            break;
                        case "idols" when id is { Length: 10 }:
                            localLabels.Add((CharacterIdOfCard(id), label));
                            break;
                    }

                    if (Text(line, "speaker") is { } speaker)
                    {
                        localSpeakers.Add((label, speaker));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return;
            }

            lock (gate)
            {
                foreach (var (id, label) in localLabels)
                {
                    Tally(labels, id, label);
                }

                foreach (var (label, speaker) in localSpeakers)
                {
                    Tally(speakers, label, speaker);
                }
            }
        });

        var characters = new Dictionary<string, CharacterName>(StringComparer.Ordinal);
        foreach (var (id, votes) in labels)
        {
            var label = Winner(votes)!;

            // A line can animate one character while another speaks, so take the majority speaker.
            characters[id] = new CharacterName(label, speakers.TryGetValue(label, out var names) ? Winner(names) : null);
        }

        return new NameIndex(characters);
    }

    private static string? Text(JsonElement line, string property) =>
        line.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static void Tally(Dictionary<string, Dictionary<string, int>> table, string key, string value)
    {
        if (!table.TryGetValue(key, out var votes))
        {
            table[key] = votes = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        votes[value] = votes.GetValueOrDefault(value) + 1;
    }

    private static string? Winner(Dictionary<string, int> votes) =>
        votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key).FirstOrDefault();
}
