using DeployGuard.Api.Alerting;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Monitoring;
using Microsoft.Extensions.Options;

namespace DeployGuard.Tests.Unit;

public class AlertingTests
{
    private sealed class Options(AlertingOptions value) : IOptionsMonitor<AlertingOptions>
    {
        public AlertingOptions CurrentValue => value;
        public AlertingOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<AlertingOptions, string?> listener) => null;
    }

    private static readonly HealthCheckOutcome Up = new(CheckStatus.Up, 200, 50, CheckErrorType.None, null);
    private static readonly HealthCheckOutcome Degraded = new(CheckStatus.Degraded, 200, 3000, CheckErrorType.None, null);
    private static readonly HealthCheckOutcome Down = new(CheckStatus.Down, null, 5000, CheckErrorType.Timeout, "Sem resposta.");

    private static IncidentTracker Tracker(int threshold = 2) => new(new Options(new AlertingOptions { FailureThreshold = threshold }));

    [Fact]
    public void Single_failure_below_threshold_does_not_open_incident()
    {
        Assert.Equal((1, IncidentTransition.None), Tracker().Evaluate(0, false, Down));
    }

    [Fact]
    public void Reaching_threshold_opens_incident()
    {
        Assert.Equal((2, IncidentTransition.Open), Tracker().Evaluate(1, false, Down));
    }

    [Fact]
    public void Threshold_of_one_opens_on_first_failure()
    {
        Assert.Equal((1, IncidentTransition.Open), Tracker(threshold: 1).Evaluate(0, false, Down));
    }

    [Fact]
    public void More_failures_with_open_incident_do_not_open_another()
    {
        Assert.Equal((5, IncidentTransition.None), Tracker().Evaluate(4, true, Down));
    }

    [Fact]
    public void Success_resolves_open_incident_and_resets_counter()
    {
        Assert.Equal((0, IncidentTransition.Resolve), Tracker().Evaluate(7, true, Up));
    }

    [Fact]
    public void Degraded_counts_as_available()
    {
        Assert.Equal((0, IncidentTransition.Resolve), Tracker().Evaluate(3, true, Degraded));
        Assert.Equal((0, IncidentTransition.None), Tracker().Evaluate(1, false, Degraded));
    }

    [Fact]
    public void Backoff_doubles_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AlertDispatcher.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), AlertDispatcher.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(120), AlertDispatcher.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(1), AlertDispatcher.Backoff(20));
    }

    [Fact]
    public void Messages_describe_the_event()
    {
        var start = new DateTimeOffset(2026, 10, 28, 14, 0, 0, TimeSpan.Zero);
        var opened = new AlertMessage(AlertEventType.IncidentOpened, 1, Guid.NewGuid(), "Checkout", start, null,
            CheckErrorType.Timeout, "Sem resposta em 5s.", "http://localhost:8080/applications/x");
        var resolved = opened with { EventType = AlertEventType.IncidentResolved, ResolvedAt = start.AddMinutes(4).AddSeconds(10) };

        Assert.Contains("Checkout está FORA DO AR", opened.Text());
        Assert.Contains("Sem resposta em 5s.", opened.Text());
        Assert.Contains("http://localhost:8080/applications/x", opened.Text());
        Assert.Contains("voltou ao ar", resolved.Text());
        Assert.Contains("4min 10s", resolved.Text());
    }
}
