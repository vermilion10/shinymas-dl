using System.Text.RegularExpressions;
using ShinymasDl.Core.Catalog;
using ShinymasDl.Core.Crypto;
using ShinymasDl.Core.Naming;

namespace ShinymasDl.Core.Extraction;

public sealed record ExtractOptions(string RawRoot, string OutputRoot, bool Overwrite, int Concurrency);

public sealed record ExtractStats(int Written, int Skipped, int Failed, int Scripts);

/// <summary> turns the raw mirror into a tree grouped by character, card and story, decrypting text assets on the way </summary>
public sealed partial class Extractor(ExtractOptions options, NameIndex names, Action<string> log)
{
    private int _written;
    private int _skipped;
    private int _failed;
    private int _scripts;

    public ExtractStats Run(AssetFilter filter)
    {
        var files = Directory.EnumerateFiles(options.RawRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(options.RawRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.StartsWith('.') && !path.EndsWith(".part", StringComparison.Ordinal))
            .Where(path => filter.Matches(new AssetEntry(path, "")));

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Concurrency) };
        Parallel.ForEach(files, parallelOptions, ExtractOne);

        return new ExtractStats(_written, _skipped, _failed, _scripts);
    }

    /// <summary> where a raw path lands in the organized tree, relative to the output root </summary>
    public string Route(string path)
    {
        if (CardAsset().Match(path) is { Success: true } card)
        {
            return $"{CardFolder(card)}/{Kind(card)}.{card.Groups["ext"].Value}";
        }

        if (CardSpine().Match(path) is { Success: true } cardSpine)
        {
            return $"{CardFolder(cardSpine)}/spine/{Kind(cardSpine)}/{cardSpine.Groups["file"].Value}";
        }

        if (CardMovie().Match(path) is { Success: true } movie)
        {
            return $"{CardFolder(movie)}/{movie.Groups["kind"].Value}.mp4";
        }

        if (CardVoice().Match(path) is { Success: true } cardVoice)
        {
            return $"{CardFolder(cardVoice)}/voice/{cardVoice.Groups["file"].Value}";
        }

        if (CharacterAsset().Match(path) is { Success: true } character)
        {
            return $"characters/{names.CharacterFolder(character.Groups["id"].Value)}/{character.Groups["kind"].Value}.{character.Groups["ext"].Value}";
        }

        if (CharacterSpine().Match(path) is { Success: true } characterSpine)
        {
            return $"characters/{names.CharacterFolder(characterSpine.Groups["id"].Value)}/spine/" +
                   $"{characterSpine.Groups["kind"].Value}/{characterSpine.Groups["file"].Value}";
        }

        if (CharacterVoice().Match(path) is { Success: true } characterVoice)
        {
            return $"characters/{names.CharacterFolder(characterVoice.Groups["id"].Value)}/voice/{characterVoice.Groups["file"].Value}";
        }

        if (Script().Match(path) is { Success: true } script && script.Groups["type"].Value != "support_skills")
        {
            return $"scenarios/{script.Groups["type"].Value}/{script.Groups["id"].Value}/script.json";
        }

        if (ScriptVoice().Match(path) is { Success: true } scriptVoice)
        {
            return $"scenarios/{scriptVoice.Groups["type"].Value}/{scriptVoice.Groups["id"].Value}/voice/{scriptVoice.Groups["file"].Value}";
        }

        return "other/" + path;
    }

    private string CardFolder(Match match)
    {
        var group = match.Groups["group"].Value == "support_idols" ? "support_idols" : "idols";
        var card = match.Groups["card"].Value;
        return $"{group}/{names.CharacterFolder(NameIndex.CharacterIdOfCard(card))}/{card}";
    }

    // Awake cards reuse their base card id, so their files share its folder under an awake_ prefix.
    private static string Kind(Match match) =>
        (match.Groups["group"].Value == "awake_idols" ? "awake_" : "") + match.Groups["kind"].Value;

    private void ExtractOne(string path)
    {
        var route = Route(path);
        var destination = Path.Combine(options.OutputRoot, route.Replace('/', Path.DirectorySeparatorChar));
        var source = Path.Combine(options.RawRoot, path.Replace('/', Path.DirectorySeparatorChar));

        if (!options.Overwrite && File.Exists(destination))
        {
            Interlocked.Increment(ref _skipped);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (!AssetCrypto.IsEncrypted(path))
            {
                File.Copy(source, destination, overwrite: true);
            }
            else
            {
                var plain = AssetCrypto.Decrypt(File.ReadAllBytes(source));
                File.WriteAllBytes(destination, plain);

                if (route.StartsWith("scenarios/", StringComparison.Ordinal)
                    && Transcript.TryWrite(plain, Path.ChangeExtension(destination, ".txt")))
                {
                    Interlocked.Increment(ref _scripts);
                }
            }

            Interlocked.Increment(ref _written);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _failed);
            log($"  failed {path}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"^images/content/(?<group>idols|support_idols|awake_idols)/(?<kind>[^/]+)/(?<card>[12]\d{9})\.(?<ext>\w+)$")]
    private static partial Regex CardAsset();

    [GeneratedRegex(@"^spine/(?<group>idols|support_idols|awake_idols)/(?<kind>[^/]+)/(?<card>[12]\d{9})/(?<file>[^/]+)$")]
    private static partial Regex CardSpine();

    [GeneratedRegex(@"^movies/(?<group>idols|support_idols)/(?<kind>[^/]+)/(?<card>[12]\d{9})\.mp4$")]
    private static partial Regex CardMovie();

    [GeneratedRegex(@"^sounds/voice/(?<group>idols|support_idols)/(?<card>[12]\d{9})/(?<file>[^/]+)$")]
    private static partial Regex CardVoice();

    [GeneratedRegex(@"^images/content/characters/(?<kind>[^/]+)/(?<id>\d{3})\.(?<ext>\w+)$")]
    private static partial Regex CharacterAsset();

    [GeneratedRegex(@"^spine/characters/(?<kind>[^/]+)/(?<id>\d{3})/(?<file>[^/]+)$")]
    private static partial Regex CharacterSpine();

    [GeneratedRegex(@"^sounds/voice/characters/(?<id>\d{3})/(?<file>[^/]+)$")]
    private static partial Regex CharacterVoice();

    [GeneratedRegex(@"^json/(?<type>[^/]+)/(?<id>[^/]+)\.json$")]
    private static partial Regex Script();

    [GeneratedRegex(@"^sounds/voice/events/(?<type>[^/]+)/(?<id>[^/]+)/(?<file>[^/]+)$")]
    private static partial Regex ScriptVoice();
}
