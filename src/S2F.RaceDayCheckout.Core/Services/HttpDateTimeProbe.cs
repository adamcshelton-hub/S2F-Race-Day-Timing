using System.Globalization;
using System.Net.Http.Headers;

namespace S2F.RaceDayCheckout.Core.Services;

public readonly record struct HttpDateProbeResult(
    bool Success,
    DateTimeOffset? ReferenceUtc,
    TimeSpan? AbsoluteSkew,
    string Detail);

/// <summary>
/// Estimates true UTC from HTTPS <c>Date</c> response headers (no NTP / UDP 123).
/// Cellular-friendly fallback when measuring skew or feeding <c>Set-Date</c>.
/// </summary>
public sealed class HttpDateTimeProbe
{
    /// <summary>Public endpoints that return a Date header quickly. First success wins.</summary>
    public static readonly string[] DefaultUrls =
    [
        "https://www.microsoft.com/",
        "https://www.cloudflare.com/",
        "https://www.google.com/"
    ];

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _utcNow;

    public HttpDateTimeProbe(HttpClient? http = null, Func<DateTimeOffset>? utcNow = null)
    {
        _http = http ?? CreateDefaultClient();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = SystemTimeSyncPolicy.ProbeBudget };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("S2FRaceDayCheckout/1.0");
        return client;
    }

    public async Task<HttpDateProbeResult> ProbeAsync(
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? urls = null)
    {
        var list = urls ?? DefaultUrls;
        Exception? lastError = null;

        foreach (var url in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                // Some CDNs dislike HEAD — fall through to GET on failure below.
                using var head = await _http.SendAsync(
                    req,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (TryReadDate(head.Headers, out var fromHead))
                    return Success(fromHead, "HEAD " + url);

                // Retry once with GET if HEAD had no Date / was rejected.
                if (!head.IsSuccessStatusCode || !head.Headers.Date.HasValue)
                {
                    using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                    using var get = await _http.SendAsync(
                        getReq,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (TryReadDate(get.Headers, out var fromGet))
                        return Success(fromGet, "GET " + url);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        return new HttpDateProbeResult(
            false,
            null,
            null,
            lastError?.Message ?? "No HTTPS Date header from probe hosts.");
    }

    private HttpDateProbeResult Success(DateTimeOffset referenceUtc, string detail)
    {
        var now = _utcNow();
        var skew = SystemTimeSyncPolicy.AbsoluteSkew(referenceUtc, now);
        return new HttpDateProbeResult(true, referenceUtc.ToUniversalTime(), skew, detail);
    }

    public static bool TryReadDate(HttpResponseHeaders headers, out DateTimeOffset utc)
    {
        if (headers.Date is { } dated)
        {
            utc = dated.ToUniversalTime();
            return true;
        }

        if (headers.TryGetValues("Date", out var values))
        {
            foreach (var raw in values)
            {
                if (DateTimeOffset.TryParseExact(
                        raw,
                        "r",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsed)
                    || DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out parsed))
                {
                    utc = parsed.ToUniversalTime();
                    return true;
                }
            }
        }

        utc = default;
        return false;
    }
}
