using System.Net;
using System.Net.Http.Headers;
using S2F.RaceDayCheckout.Core.Services;

namespace S2F.RaceDayCheckout.Core.Tests;

public class HttpDateTimeProbeTests
{
    [Fact]
    public async Task ProbeAsync_UsesDateHeader_AndReportsSkew()
    {
        var reference = new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        var local = reference.AddSeconds(-12);
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Date = reference;
            return response;
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var probe = new HttpDateTimeProbe(http, () => local);
        var result = await probe.ProbeAsync(urls: ["https://example.test/"]);

        Assert.True(result.Success);
        Assert.Equal(reference, result.ReferenceUtc);
        Assert.Equal(TimeSpan.FromSeconds(12), result.AbsoluteSkew);
    }

    [Fact]
    public async Task ProbeAsync_FallsThroughHosts_WhenFirstFails()
    {
        var reference = new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);
        var n = 0;
        var handler = new StubHandler(_ =>
        {
            n++;
            if (n == 1)
                throw new HttpRequestException("down");
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Date = reference;
            return response;
        });

        using var http = new HttpClient(handler);
        var probe = new HttpDateTimeProbe(http, () => reference);
        var result = await probe.ProbeAsync(urls: ["https://a.test/", "https://b.test/"]);

        Assert.True(result.Success);
        Assert.Equal(2, n);
        Assert.Equal(TimeSpan.Zero, result.AbsoluteSkew);
    }

    [Fact]
    public void TryReadDate_ParsesRfc1123()
    {
        var headers = new HttpResponseMessage(HttpStatusCode.OK).Headers;
        headers.TryAddWithoutValidation("Date", "Tue, 27 Oct 2026 18:00:00 GMT");
        Assert.True(HttpDateTimeProbe.TryReadDate(headers, out var utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero), utc);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
