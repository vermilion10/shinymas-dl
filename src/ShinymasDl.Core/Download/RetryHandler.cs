using System.Net;

namespace ShinymasDl.Core.Download;

/// <summary> retries transient GET failures with exponential backoff (0.5s, 1s, 2s) </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private const int MaxRetries = 3;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt - 1)), cancellationToken);
            }

            // an HttpRequestMessage cannot be sent twice
            var attemptRequest = attempt == 0 ? request : Clone(request);
            HttpResponseMessage response;

            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                continue;
            }
            catch (TaskCanceledException) when (attempt < MaxRetries && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (request.Method != HttpMethod.Get
                || !RetryableStatusCodes.Contains(response.StatusCode)
                || attempt >= MaxRetries)
            {
                return response;
            }

            response.Dispose();
        }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
