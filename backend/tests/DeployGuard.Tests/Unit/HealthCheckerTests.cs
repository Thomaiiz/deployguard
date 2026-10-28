using System.Net;
using System.Net.Sockets;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Monitoring;
using DeployGuard.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DeployGuard.Tests.Unit;

public class HealthCheckerTests
{
    private readonly FakeTimeProvider _time = new();

    private HealthChecker Checker(StubHttpMessageHandler handler, Action<MonitoringOptions>? configure = null)
    {
        var options = TestOptions.With(configure);
        return new HealthChecker(
            new StubHttpClientFactory(handler),
            new UrlSafetyPolicy(options),
            options,
            _time,
            NullLogger<HealthChecker>.Instance);
    }

    private static StubHttpMessageHandler Respond(HttpStatusCode code, Action? beforeReturn = null) =>
        new((_, _) =>
        {
            beforeReturn?.Invoke();
            return Task.FromResult(new HttpResponseMessage(code));
        });

    [Fact]
    public async Task Success_response_is_up_with_latency()
    {
        var handler = Respond(HttpStatusCode.OK, () => _time.Advance(TimeSpan.FromMilliseconds(120)));

        var outcome = await Checker(handler).CheckAsync("https://example.com/health", CancellationToken.None);

        Assert.Equal(CheckStatus.Up, outcome.Status);
        Assert.Equal(200, outcome.HttpStatusCode);
        Assert.Equal(120, outcome.LatencyMs);
        Assert.Equal(CheckErrorType.None, outcome.ErrorType);
    }

    [Fact]
    public async Task Slow_success_is_degraded()
    {
        var handler = Respond(HttpStatusCode.NoContent, () => _time.Advance(TimeSpan.FromMilliseconds(1500)));

        var outcome = await Checker(handler, o => o.DegradedLatencyThresholdMs = 1000)
            .CheckAsync("https://example.com/health", CancellationToken.None);

        Assert.Equal(CheckStatus.Degraded, outcome.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)] // redirect não é seguido
    public async Task Non_success_status_is_down(HttpStatusCode code)
    {
        var outcome = await Checker(Respond(code)).CheckAsync("https://example.com/health", CancellationToken.None);

        Assert.Equal(CheckStatus.Down, outcome.Status);
        Assert.Equal((int)code, outcome.HttpStatusCode);
        Assert.Equal(CheckErrorType.UnexpectedStatusCode, outcome.ErrorType);
    }

    [Fact]
    public async Task Hanging_target_times_out()
    {
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var outcome = await Checker(handler, o => o.RequestTimeoutSeconds = 1)
            .CheckAsync("https://example.com/health", CancellationToken.None);

        Assert.Equal(CheckStatus.Down, outcome.Status);
        Assert.Equal(CheckErrorType.Timeout, outcome.ErrorType);
    }

    [Fact]
    public async Task Url_no_longer_authorized_is_not_requested()
    {
        var handler = Respond(HttpStatusCode.OK);

        var outcome = await Checker(handler).CheckAsync("https://removed-from-allowlist.com/", CancellationToken.None);

        Assert.Equal(CheckErrorType.BlockedDestination, outcome.ErrorType);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Blocked_destination_from_connector_is_classified()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("falha", new BlockedDestinationException("example.com")));

        var outcome = await Checker(handler).CheckAsync("https://example.com/", CancellationToken.None);

        Assert.Equal(CheckErrorType.BlockedDestination, outcome.ErrorType);
        Assert.DoesNotContain("falha", outcome.ErrorMessage); // mensagem bruta não vaza
    }

    [Fact]
    public void Classifies_network_errors()
    {
        Assert.Equal(CheckErrorType.DnsFailure,
            HealthChecker.Classify(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal(CheckErrorType.ConnectionFailed,
            HealthChecker.Classify(new HttpRequestException(HttpRequestError.ConnectionError, "x")));
        Assert.Equal(CheckErrorType.TlsFailure,
            HealthChecker.Classify(new HttpRequestException(HttpRequestError.SecureConnectionError, "x")));
    }
}
