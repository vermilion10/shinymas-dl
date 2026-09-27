using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShinymasDl.Core.Api;

public sealed record ApiResponse(int StatusCode, JsonNode? Body)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary> the game's error code (1010 unauthorized, 1012 stale session, ...) when the body carries one </summary>
    public int? ErrorCode => Body?["status"]?.GetValueKind() == JsonValueKind.Number ? Body["status"]!.GetValue<int>() : null;
}

/// <summary>
/// Talks to the game API the way the web client does. The enza session cookie, the game token and the
/// rotating session id stay in private fields: nothing here logs, persists or puts them in exception text.
/// </summary>
public sealed class GameApiClient : IDisposable
{
    private const string PlatformRoot = "https://platform-sdk.enza.fun";
    private const string SiteOrigin = "https://shinycolors.enza.fun";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36";

    private readonly ClientBundle _bundle;
    private readonly RequestCodec _codec;
    private readonly string _enzaSession;
    private readonly HttpClient _http;
    private readonly List<KeyValuePair<string, string>> _headers = [];

    public GameApiClient(ClientBundle bundle, RequestCodec codec, string enzaSession)
    {
        _bundle = bundle;
        _codec = codec;
        _enzaSession = enzaSession;

        // No cookie container: the platform's Set-Cookie responses are dropped, not kept.
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(SiteOrigin + "/");
        _http.DefaultRequestHeaders.Add("Origin", SiteOrigin);
    }

    /// <summary> mints a game token from the enza session, then logs in and opens the game top as the client does </summary>
    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var gameToken = await MintGameTokenAsync(cancellationToken);
        SetHeader("Authorization", gameToken);
        SetHeader("x-version", _bundle.XVersion);
        SetHeader("x-em-version", _bundle.EmVersion);

        var login = await SendAsync(HttpMethod.Post, "login", cancellationToken: cancellationToken);
        if (!login.IsSuccess)
        {
            throw new InvalidOperationException($"Game login failed: HTTP {login.StatusCode}, code {login.ErrorCode}.");
        }

        if (login.Body?["isCreated"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("The account has no game profile yet; play through the opening once in a browser.");
        }

        var top = await SendAsync(HttpMethod.Get, "gameTop", cancellationToken: cancellationToken);
        if (!top.IsSuccess)
        {
            throw new InvalidOperationException($"gameTop failed: HTTP {top.StatusCode}, code {top.ErrorCode}.");
        }
    }

    public async Task<ApiResponse> SendAsync(
        HttpMethod method, string path, JsonObject? parameters = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendOnceAsync(method, path, parameters, cancellationToken);

            // Same retry rule as the client: GET only, at most three times.
            if (response.IsSuccess || method != HttpMethod.Get || attempt >= 3)
            {
                return response;
            }

            switch (response.ErrorCode)
            {
                case 1012:
                    continue;
                case 1090:
                    await Task.Delay(80, cancellationToken);
                    continue;
                default:
                    return response;
            }
        }
    }

    private async Task<ApiResponse> SendOnceAsync(
        HttpMethod method, string path, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var raw = new StringBuilder($"{method.Method.ToUpperInvariant()} /{path} HTTP/1.1\r\n");
        if (method != HttpMethod.Get)
        {
            raw.Append("Content-Type: application/json\r\n");
        }

        foreach (var (name, value) in _headers)
        {
            raw.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (parameters is not null)
        {
            var json = parameters.ToJsonString();
            raw.Append("Content-Length: ").Append(json.Length).Append("\r\n\r\n").Append(json);
        }
        else
        {
            raw.Append("\r\n");
        }

        using var content = new ByteArrayContent(_codec.Encode(raw.ToString()));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _http.PostAsync(_bundle.ApiRoot, content, cancellationToken);

        if (response.Headers.Contains("x-is-banned"))
        {
            throw new InvalidOperationException("The API flagged this account as banned; stopping.");
        }

        if (response.Headers.Contains("x-maintenance"))
        {
            throw new InvalidOperationException("The game is in maintenance; try again later.");
        }

        var sessionId = response.Headers.TryGetValues("x-sessionid", out var values) ? values.FirstOrDefault() ?? "" : "";
        if (sessionId.Length > 0)
        {
            SetHeader("x-sessionid", sessionId);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        JsonNode? body = null;
        if (bytes.Length > 0 && response.Content.Headers.ContentType?.MediaType == "application/octet-stream")
        {
            body = JsonNode.Parse(_codec.Decode(bytes, sessionId));
        }

        return new ApiResponse((int)response.StatusCode, body);
    }

    private async Task<string> MintGameTokenAsync(CancellationToken cancellationToken)
    {
        var url = $"{PlatformRoot}/sessions/user_code?app_id={_bundle.GameId}&ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", "_enza_session=" + _enzaSession);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The enza platform refused the session (HTTP {(int)response.StatusCode}); copy a fresh _enza_session.");
        }

        var token = JsonNode.Parse(text)?["token"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("The enza platform returned no game token.");

        var claims = JwtClaims(token);
        if (claims?["is_guest"]?.GetValue<bool>() == true || claims?["is_game_user_registered"]?.GetValue<bool>() == false)
        {
            throw new InvalidOperationException(
                "SHINYMAS_SESSION maps to a guest identity, not your account. Copy _enza_session from a " +
                "platform-sdk.enza.fun request while logged in.");
        }

        return token;
    }

    private static JsonNode? JwtClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonNode.Parse(Convert.FromBase64String(payload));
    }

    private void SetHeader(string name, string value)
    {
        var index = _headers.FindIndex(h => h.Key == name);
        if (index >= 0)
        {
            _headers[index] = new KeyValuePair<string, string>(name, value);
        }
        else
        {
            _headers.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    public void Dispose() => _http.Dispose();
}
