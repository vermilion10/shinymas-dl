using System.Text.Json.Nodes;

namespace ShinymasDl.Core.Api;

public sealed record AlbumSummary(
    string CharacterId,
    int Cards,
    int UnownedCards,
    int UnownedCardsWithHash,
    int Costumes,
    int Whispers,
    int LockedWhispers,
    int LockedWhispersWithHash,
    IReadOnlyList<string> WhisperFields);

/// <summary> reads every character album and records card, costume and whisper hashes, saving after each character </summary>
public sealed class AlbumCrawler(GameApiClient client, CardHashes store, string dataRoot, Action<string> log)
{
    public async Task<IReadOnlyList<AlbumSummary>> RunAsync(
        IReadOnlySet<string>? only, bool refresh, TimeSpan delay, CancellationToken cancellationToken)
    {
        var top = await client.SendAsync(HttpMethod.Get, "album/top", cancellationToken: cancellationToken);
        if (!top.IsSuccess)
        {
            throw new InvalidOperationException($"album/top failed: HTTP {top.StatusCode}, code {top.ErrorCode}.");
        }

        var characters = (top.Body?["characters"]?.AsArray() ?? [])
            .Select(c => c?["id"] is JsonValue value ? value.ToString() : null)
            .OfType<string>()
            .Where(id => only is null || only.Contains(Normalize(id)))
            .ToList();

        log($"Album lists {top.Body?["characters"]?.AsArray().Count ?? 0} characters; {characters.Count} selected");

        var summaries = new List<AlbumSummary>();
        foreach (var id in characters)
        {
            if (!refresh && store.Characters.ContainsKey(id))
            {
                log($"  {id}: already recorded, skipping");
                continue;
            }

            var album = await client.SendAsync(
                HttpMethod.Post, $"characterAlbums/characters/{id}", cancellationToken: cancellationToken);
            if (!album.IsSuccess || album.Body is null)
            {
                throw new InvalidOperationException(
                    $"Album for character {id} failed: HTTP {album.StatusCode}, code {album.ErrorCode}. Progress so far is saved.");
            }

            var (hashes, summary) = Read(id, album.Body);
            store.Characters[id] = hashes;
            store.Save(dataRoot);
            summaries.Add(summary);

            await Task.Delay(delay, cancellationToken);
        }

        return summaries;
    }

    private static (CharacterHashes, AlbumSummary) Read(string characterId, JsonNode album)
    {
        var hashes = new CharacterHashes();
        int cards = 0, unowned = 0, unownedWithHash = 0, whispers = 0, locked = 0, lockedWithHash = 0;
        var whisperFields = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (listName, ownedFlag) in new[] { ("produceIdols", "hasIdol"), ("supportIdols", "hasSupportIdol") })
        {
            foreach (var card in Items(album[listName]))
            {
                cards++;
                var owned = card[ownedFlag]?.GetValue<bool>() == true;
                var hash = Text(card, "hash");
                if (!owned)
                {
                    unowned++;
                    unownedWithHash += hash is null ? 0 : 1;
                }

                if (Text(card, "id") is { } cardId && hash is not null)
                {
                    hashes.Cards[cardId] = hash;
                }

                foreach (var voice in Items(card["idolWhisperVoices"]))
                {
                    whispers++;
                    whisperFields.UnionWith(voice.AsObject().Select(p => p.Key));
                    var voiceHash = Text(voice, "voiceHash");

                    // Without the card every whisper is locked; release is otherwise decided by evolution stage on the client.
                    if (!owned)
                    {
                        locked++;
                        lockedWithHash += voiceHash is null ? 0 : 1;
                    }

                    if (Text(voice, "id") is { } voiceId && voiceHash is not null)
                    {
                        hashes.Whispers[voiceId] = voiceHash;
                    }
                }
            }
        }

        foreach (var costume in Items(album["idolCostumes"]))
        {
            if (Text(costume, "id") is { } costumeId && Text(costume, "hash") is { } costumeHash)
            {
                hashes.Costumes[costumeId] = costumeHash;
            }

            if (Text(costume, "evolutionSkinId") is { } skinId && skinId != "0" && Text(costume, "evolutionSkinHash") is { } skinHash)
            {
                hashes.EvolutionSkins[skinId] = skinHash;
            }
        }

        var summary = new AlbumSummary(
            characterId, cards, unowned, unownedWithHash, hashes.Costumes.Count,
            whispers, locked, lockedWithHash, [.. whisperFields]);
        return (hashes, summary);
    }

    public static string Normalize(string id) => id.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0";

    private static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonNode>() : [];

    private static string? Text(JsonNode node, string property) =>
        node[property] is JsonValue value && value.ToString() is { Length: > 0 } text ? text : null;
}
