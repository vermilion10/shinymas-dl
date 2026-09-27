using System.Text.RegularExpressions;
using ShinymasDl.Core.Api;

namespace ShinymasDl.Core.Catalog;

/// <summary> points entries at their <c>&lt;hash&gt;_&lt;id&gt;</c> CDN path when <c>albums</c> recorded a hash for that card or whisper voice </summary>
public sealed partial class HashedPaths(IReadOnlyDictionary<string, string> cards, IReadOnlyDictionary<string, string> whispers)
{
    public static HashedPaths Load(string dataRoot)
    {
        var store = CardHashes.Load(dataRoot);
        var whispers = store.AllWhispers().ToDictionary(w => AlbumCrawler.Normalize(w.Key), w => w.Value, StringComparer.Ordinal);
        return new HashedPaths(store.AllCards(), whispers);
    }

    public int CardCount => cards.Count;

    public int WhisperCount => whispers.Count;

    public AssetEntry Apply(AssetEntry entry)
    {
        // Card art and movies are hashed per card; spine and voice files under the same ids are not.
        if (CardFile().Match(entry.Path) is { Success: true } card && cards.TryGetValue(card.Groups["id"].Value, out var cardHash))
        {
            return entry with { CdnPath = $"{card.Groups["dir"].Value}/{cardHash}_{card.Groups["file"].Value}" };
        }

        if (WhisperFile().Match(entry.Path) is { Success: true } voice
            && whispers.TryGetValue(AlbumCrawler.Normalize(voice.Groups["id"].Value), out var voiceHash))
        {
            return entry with { CdnPath = $"{voice.Groups["dir"].Value}/{voiceHash}_{voice.Groups["file"].Value}" };
        }

        return entry;
    }

    [GeneratedRegex(@"^(?<dir>(?:images/content/(?:idols|support_idols|awake_idols)|movies/(?:idols|support_idols))/[^/]+)/(?<file>(?<id>[12]\d{9})\.\w+)$")]
    private static partial Regex CardFile();

    [GeneratedRegex(@"^(?<dir>sounds/voice/whisper/\d+)/(?<file>(?<id>\d+)\.m4a)$")]
    private static partial Regex WhisperFile();
}
