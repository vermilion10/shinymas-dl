using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;

namespace ShinymasDl.Core.Api;

/// <summary> runs the game's own asm.js <c>request_hash</c> module, keyed by the clock second for requests and by the session id for responses </summary>
public sealed partial class RequestCodec
{
    private const string Glue = """
        var shinyCodec = (function () {
          var queue = self[CHUNK_GLOBAL];
          var modules = queue[queue.length - 1][1];
          var key = Object.keys(modules).filter(function (k) { return modules[k].length === 3; })[0];
          var holder = { exports: {} };
          modules[key](holder, {}, function () { return {}; });
          var M = {};
          holder.exports(M);
          return M;
        })();

        function shinyToLatin1(bytes) {
          var out = '';
          for (var i = 0; i < bytes.length; i += 8192) {
            out += String.fromCharCode.apply(null, Array.prototype.slice.call(bytes.subarray(i, i + 8192)));
          }
          return out;
        }

        function shinyEncode(raw) {
          var buffer = shinyCodec.encodeRequest(raw);
          var bytes = shinyCodec.HEAPU8.subarray(buffer.data(), buffer.data() + buffer.size());
          var text = shinyToLatin1(bytes);
          if (buffer.delete) buffer.delete();
          return text;
        }

        function shinyDecode(latin1, sessionId) {
          var n = latin1.length, p = shinyCodec._malloc(n);
          for (var i = 0; i < n; i++) shinyCodec.HEAPU8[p + i] = latin1.charCodeAt(i);
          try { return shinyCodec.decodeResponse(p, n, sessionId); } finally { shinyCodec._free(p); }
        }
        """;

    private readonly string _source;
    private readonly string _chunkGlobal;
    private Engine? _engine;

    public RequestCodec(string chunkPath)
    {
        _source = File.ReadAllText(chunkPath);
        _chunkGlobal = ChunkGlobal().Match(_source) is { Success: true } match
            ? match.Groups["v"].Value
            : throw new InvalidOperationException("Unrecognized request_hash chunk format.");
    }

    public byte[] Encode(string rawRequest) => Latin1(Call("shinyEncode", rawRequest));

    /// <summary> the module returns UTF-8 bytes as a binary string, which the client turns into text with escape/decodeURIComponent </summary>
    public string Decode(byte[] body, string sessionId) =>
        Encoding.UTF8.GetString(Latin1(Call("shinyDecode", Encoding.Latin1.GetString(body), sessionId)));

    private string Call(string function, params object[] arguments)
    {
        try
        {
            return (_engine ??= Load()).Invoke(function, arguments).AsString();
        }
        catch (JavaScriptException ex)
        {
            // An emscripten abort leaves the module unusable, so the next call starts a fresh engine.
            _engine = null;
            throw new InvalidDataException($"Request codec failed: {ex.Message}", ex);
        }
    }

    private Engine Load()
    {
        var engine = new Engine(options => options.LimitMemory(512_000_000));
        engine.Execute("var self = globalThis; var window = globalThis;");
        engine.Execute(_source);
        engine.Execute(Glue.Replace("CHUNK_GLOBAL", $"'{_chunkGlobal}'"));
        return engine;
    }

    private static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    [GeneratedRegex(@"\(self\.(?<v>[A-Za-z0-9_$]+)=self\.")]
    private static partial Regex ChunkGlobal();
}
