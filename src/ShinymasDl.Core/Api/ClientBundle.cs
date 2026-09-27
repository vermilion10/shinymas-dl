using System.Text.Json;
using System.Text.RegularExpressions;
using ShinymasDl.Core.Download;

namespace ShinymasDl.Core.Api;

/// <summary> the values the API expects that live in the web client: API root, game id, version headers and the request codec chunk </summary>
public sealed partial record ClientBundle(
    string AppScript,
    string ApiRoot,
    int GameId,
    string XVersion,
    string EmVersion,
    string CodecChunk)
{
    private const string SiteRoot = "https://shinycolors.enza.fun/";

    public string CodecPath(string dataRoot) => Path.Combine(dataRoot, "client", CodecChunk);

    /// <summary> reuses <c>data/client/bundle.json</c> while the live index still points at the same app script </summary>
    public static async Task<ClientBundle> ResolveAsync(string dataRoot, Action<string> log, CancellationToken cancellationToken)
    {
        var http = ShinyHttpClient.Instance;
        var clientRoot = Path.Combine(dataRoot, "client");
        var cachePath = Path.Combine(clientRoot, "bundle.json");

        var index = await http.GetStringAsync(SiteRoot, cancellationToken);
        var appScript = AppScriptPattern().Match(index) is { Success: true } app
            ? app.Value
            : throw new InvalidOperationException("The game index no longer references an app-*.js bundle.");

        if (File.Exists(cachePath)
            && JsonSerializer.Deserialize<ClientBundle>(await File.ReadAllTextAsync(cachePath, cancellationToken)) is { } cached
            && cached.AppScript == appScript
            && File.Exists(cached.CodecPath(dataRoot)))
        {
            return cached;
        }

        log($"Reading client bundle {appScript}");
        var env = await http.GetStringAsync(SiteRoot + "env.js", cancellationToken);
        var source = await http.GetStringAsync(SiteRoot + appScript, cancellationToken);

        var bundle = new ClientBundle(
            appScript,
            Require(ApiRootPattern().Match(env), "API_ROOT in env.js"),
            int.Parse(Require(GameIdPattern().Match(env), "GAME_ID in env.js")),
            Require(XVersionPattern().Match(source), "X_VERSION"),
            Require(EmVersionPattern().Match(source), "x-em-version"),
            FindCodecChunk(source));

        Directory.CreateDirectory(clientRoot);
        var chunk = await http.GetByteArrayAsync(SiteRoot + bundle.CodecChunk, cancellationToken);
        await File.WriteAllBytesAsync(bundle.CodecPath(dataRoot), chunk, cancellationToken);
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(bundle), cancellationToken);
        return bundle;
    }

    /// <summary> webpack names chunks <c>{name}-{hash}.chunk.js</c> from two id maps; the asm.js build is the one named plain <c>request_hash</c> </summary>
    private static string FindCodecChunk(string source)
    {
        var id = Require(CodecChunkIdPattern().Match(source), "the request_hash chunk id");
        var hashes = Require(ChunkHashMapPattern().Match(source), "the webpack chunk hash map");
        var hash = Regex.Match(hashes, $@"(?:^|,){id}:""(?<v>[0-9a-f]{{20}})""");
        return hash.Success
            ? $"request_hash-{hash.Groups["v"].Value}.chunk.js"
            : throw new InvalidOperationException("No chunk hash for request_hash in the app bundle.");
    }

    private static string Require(Match match, string what) =>
        match.Success
            ? match.Groups["v"].Value
            : throw new InvalidOperationException($"Could not find {what} in the web client; the game client changed.");

    [GeneratedRegex(@"app-[0-9a-f]+\.js")]
    private static partial Regex AppScriptPattern();

    [GeneratedRegex(@"API_ROOT:\s*""(?<v>[^""]+)""")]
    private static partial Regex ApiRootPattern();

    [GeneratedRegex(@"GAME_ID:\s*(?<v>\d+)")]
    private static partial Regex GameIdPattern();

    [GeneratedRegex(@"X_VERSION:'(?<v>\d+)'")]
    private static partial Regex XVersionPattern();

    [GeneratedRegex(@"\|\|this\.addHeader\([A-Za-z_$]+,""(?<v>[0-9a-f]{40})""\)")]
    private static partial Regex EmVersionPattern();

    [GeneratedRegex(@"(?<v>\d+):""request_hash""")]
    private static partial Regex CodecChunkIdPattern();

    [GeneratedRegex(@"\+""-""\+\{(?<v>[^}]*)\}\[e\]\+""\.chunk\.js""")]
    private static partial Regex ChunkHashMapPattern();
}
