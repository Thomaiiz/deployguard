using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Incidents;
using DeployGuard.Api.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class IncidentsApiTests(IncidentApiFactory factory) : IClassFixture<IncidentApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HealthCheckOutcome Up = new(CheckStatus.Up, 200, 40, CheckErrorType.None, null);
    private static readonly HealthCheckOutcome Down = new(CheckStatus.Down, 503, 30, CheckErrorType.UnexpectedStatusCode, "HTTP 503");

    private readonly HttpClient _client = factory.CreateClient();

    private async Task<ApplicationResponse> CreateAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest(name, "https://api.example.com/health", 30));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;
    }

    private async Task RunRoundAsync(HealthCheckOutcome outcome)
    {
        factory.HealthChecker.NextOutcome = outcome;
        await factory.MakeEverythingDueAsync();
        await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Incident_lifecycle_opens_alerts_and_resolves()
    {
        var app = await CreateAsync("Pagamentos");
        factory.AlertSender.Fail = false;

        // 1ª falha: abaixo do limiar (2), nada de incidente.
        await RunRoundAsync(Down);
        Assert.Empty(await _client.GetFromJsonAsync<IncidentResponse[]>($"/api/applications/{app.Id}/incidents", Json) ?? []);

        // 2ª falha: abre incidente e enfileira alerta.
        await RunRoundAsync(Down);
        var open = await _client.GetFromJsonAsync<IncidentResponse[]>("/api/incidents?status=Open", Json);
        var incident = Assert.Single(open!, i => i.ApplicationId == app.Id);
        Assert.True(incident.IsOpen);
        Assert.Equal(CheckErrorType.UnexpectedStatusCode, incident.Cause);

        var current = await _client.GetFromJsonAsync<ApplicationResponse>($"/api/applications/{app.Id}", Json);
        Assert.NotNull(current!.OpenIncidentSince);

        // 3ª falha: continua o mesmo incidente.
        await RunRoundAsync(Down);
        Assert.Single((await _client.GetFromJsonAsync<IncidentResponse[]>($"/api/applications/{app.Id}/incidents", Json))!);

        // O dispatcher entrega o alerta de abertura.
        Assert.Equal(1, await factory.Dispatcher.DispatchPendingAsync(CancellationToken.None));
        Assert.Contains(factory.AlertSender.Sent, s => s.Message.EventType == AlertEventType.IncidentOpened
            && s.Message.ApplicationName == "Pagamentos" && s.Channel == "plantao");

        // Voltou: resolve e envia o alerta de recuperação.
        await RunRoundAsync(Up);
        await factory.MakeEverythingDueAsync();
        Assert.Equal(1, await factory.Dispatcher.DispatchPendingAsync(CancellationToken.None));
        Assert.Contains(factory.AlertSender.Sent, s => s.Message.EventType == AlertEventType.IncidentResolved
            && s.Message.ApplicationId == app.Id);

        var history = await _client.GetFromJsonAsync<IncidentResponse[]>($"/api/applications/{app.Id}/incidents", Json);
        var resolved = Assert.Single(history!);
        Assert.False(resolved.IsOpen);
        Assert.Equal(2, resolved.NotificationsSent);

        await _client.DeleteAsync($"/api/applications/{app.Id}");
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_backoff_then_given_up()
    {
        var app = await CreateAsync("Busca");
        factory.AlertSender.Fail = true;

        await RunRoundAsync(Down);
        await RunRoundAsync(Down);

        // 1ª tentativa falha: agenda nova tentativa no futuro (backoff).
        await factory.Dispatcher.DispatchPendingAsync(CancellationToken.None);
        await factory.WithDbAsync(async db =>
        {
            var n = await db.AlertNotifications.SingleAsync(x => x.Incident!.ApplicationId == app.Id);
            Assert.Equal(1, n.Attempts);
            Assert.Null(n.SentAt);
            Assert.Null(n.FailedAt);
            Assert.True(n.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20));
            Assert.Contains("HTTP 500", n.LastError);
        });

        // 2ª tentativa (máximo = 2 no teste): desiste.
        await factory.MakeEverythingDueAsync();
        await factory.Dispatcher.DispatchPendingAsync(CancellationToken.None);
        await factory.WithDbAsync(async db =>
        {
            var n = await db.AlertNotifications.SingleAsync(x => x.Incident!.ApplicationId == app.Id);
            Assert.Equal(2, n.Attempts);
            Assert.NotNull(n.FailedAt);
        });

        factory.AlertSender.Fail = false;
        await _client.DeleteAsync($"/api/applications/{app.Id}");
    }

    [Fact]
    public async Task Database_allows_only_one_open_incident_per_application()
    {
        var app = await CreateAsync("Frete");

        await factory.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { ApplicationId = app.Id, StartedAt = DateTimeOffset.UtcNow });
            db.Incidents.Add(new Incident { ApplicationId = app.Id, StartedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });

        await _client.DeleteAsync($"/api/applications/{app.Id}");
    }
}
