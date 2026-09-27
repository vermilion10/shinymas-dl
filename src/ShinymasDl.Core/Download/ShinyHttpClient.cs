using System.Net;

namespace ShinymasDl.Core.Download;

/// <summary> one pooled <see cref="HttpClient"/> for the process. the CDN is public CloudFront with no region lock </summary>
public static class ShinyHttpClient
{
    public const string DefaultAssetRoot = "https://shinycolors.enza.fun/assets/";

    private static readonly Lazy<HttpClient> LazyClient = new(Create);

    public static HttpClient Instance => LazyClient.Value;

    /// <summary> unknown paths fall through to the game's SPA index, served as HTML with a 200 </summary>
    public static bool IsSpaFallback(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType == "text/html";

    private static HttpClient Create()
    {
        var socketHandler = new SocketsHttpHandler
        {
            // above the download concurrency limit, so that limit paces the CDN rather than the pool
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
        };

        var client = new HttpClient(new RetryHandler { InnerHandler = socketHandler })
        {
            Timeout = TimeSpan.FromSeconds(120),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://shinycolors.enza.fun/");

        return client;
    }
}
