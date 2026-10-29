using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Domain;
using DeployGuard.Api.StatusPage;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class PublicStatusApiTests(StatusPageApiFactory factory) : IClassFixture<StatusPageApiFactory>
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _admin = factory.CreateAdminClient();

    internal static async Task<MonitoredApplication> CreateAppAsync(
        HttpClient admin, string name, bool isPublic, string url = "https://api.example.com/health")
    {
        var response = await admin.PostAsJsonAsync("/api/applications", new CreateApplicationRequest(name, url, 60, isPublic), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;
        return new MonitoredApplication { Id = created.Id, Name = created.Name, HealthCheckUrl = created.HealthCheckUrl };
    }

    private async Task<PublicStatusResponse> GetStatusAsync()
    {
        using var anonymous = factory.CreateClient(); // sem cookie: qualquer visitante
        var response = await anonymous.GetAsync("/api/public/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublicStatusResponse>(Json))!;
    }

    private Task ResetAsync() => factory.WithDbAsync(db => db.Applications.ExecuteDeleteAsync());

    [Fact]
    public async Task Anonymous_visitors_see_only_published_apps_and_nothing_internal()
    {
        await ResetAsync();
        var published = await CreateAppAsync(_admin, "Loja", isPublic: true, url: "https://interno-secreto.example.com/health");
        await CreateAppAsync(_admin, "Painel interno", isPublic: false);
        var paused = await CreateAppAsync(_admin, "Pausada", isPublic: true);
        await _admin.PutAsJsonAsync($"/api/applications/{paused.Id}", new UpdateApplicationRequest(null, null, null, false), Json);

        await factory.WithDbAsync(async db =>
        {
            db.HealthCheckResults.Add(new HealthCheckResult
            {
                ApplicationId = published.Id,
                CheckedAt = DateTimeOffset.UtcNow,
                Status = CheckStatus.Down,
                ErrorType = CheckErrorType.Timeout,
                ErrorMessage = "connect 10.0.0.5:443 timed out",
                LatencyMs = 5000,
            });
            await db.SaveChangesAsync();
        });

        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/api/public/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("interno-secreto", raw);       // URL
        Assert.DoesNotContain(published.Id.ToString(), raw); // Id interno
        Assert.DoesNotContain("10.0.0.5", raw);              // mensagem de erro
        Assert.DoesNotContain("latency", raw, StringComparison.OrdinalIgnoreCase);

        var status = JsonSerializer.Deserialize<PublicStatusResponse>(raw, Json)!;
        Assert.Equal(["Loja"], status.Components.Select(c => c.Name));
    }

    [Fact]
    public async Task History_has_one_bar_per_day_with_the_daily_availability()
    {
        await ResetAsync();
        var app = await CreateAppAsync(_admin, "Checkout", isPublic: true);
        var today = DateTimeOffset.UtcNow.Date;
        var yesterday = new DateTimeOffset(today.AddDays(-1).AddHours(12), TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;

        await factory.WithDbAsync(async db =>
        {
            CheckStatus[] yesterdays = [CheckStatus.Up, CheckStatus.Up, CheckStatus.Degraded, CheckStatus.Down];
            foreach (var (s, i) in yesterdays.Select((s, i) => (s, i)))
            {
                db.HealthCheckResults.Add(new HealthCheckResult { ApplicationId = app.Id, CheckedAt = yesterday.AddMinutes(i), Status = s });
            }

            db.HealthCheckResults.Add(new HealthCheckResult { ApplicationId = app.Id, CheckedAt = now, Status = CheckStatus.Up });
            db.HealthCheckResults.Add(new HealthCheckResult { ApplicationId = app.Id, CheckedAt = now.AddSeconds(-1), Status = CheckStatus.Up });
            // Mais antigo que a janela: não entra na conta.
            db.HealthCheckResults.Add(new HealthCheckResult { ApplicationId = app.Id, CheckedAt = now.AddDays(-45), Status = CheckStatus.Down });
            await db.SaveChangesAsync();
        });

        var component = (await GetStatusAsync()).Components.Single();

        // A retenção padrão é de 30 dias, então a página mostra 30 (e não os 90 configurados).
        Assert.Equal(30, component.Days.Count);
        Assert.Equal(DateOnly.FromDateTime(today), component.Days[^1].Date);
        Assert.Equal(100, component.Days[^1].AvailabilityPercent);
        Assert.Equal(75, component.Days[^2].AvailabilityPercent); // Degraded conta como disponível
        Assert.Null(component.Days[0].AvailabilityPercent);       // dia sem dados
        Assert.Equal(83.33, component.AvailabilityPercent);       // 5 de 6
    }

    [Fact]
    public async Task Only_an_open_incident_shows_an_outage()
    {
        await ResetAsync();
        var ok = await CreateAppAsync(_admin, "A ok", isPublic: true);
        var blip = await CreateAppAsync(_admin, "B oscilou", isPublic: true);
        var down = await CreateAppAsync(_admin, "C fora", isPublic: true);
        await CreateAppAsync(_admin, "D nova", isPublic: true);

        await factory.WithDbAsync(async db =>
        {
            await db.Applications.Where(a => a.Id == ok.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastStatus, CheckStatus.Up));
            await db.Applications.Where(a => a.Id == blip.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastStatus, CheckStatus.Down));
            await db.Applications.Where(a => a.Id == down.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastStatus, CheckStatus.Down));
            db.Incidents.Add(new Incident { ApplicationId = down.Id, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3), Cause = CheckErrorType.Timeout });
            await db.SaveChangesAsync();
        });

        var status = await GetStatusAsync();

        Assert.Equal(
            [ComponentStatus.Operational, ComponentStatus.Degraded, ComponentStatus.Outage, ComponentStatus.Unknown],
            status.Components.Select(c => c.Status));
        Assert.NotNull(status.Components[2].IncidentSince);
        Assert.Equal(ComponentStatus.Outage, status.Overall);
    }

    [Fact]
    public async Task Admin_publishes_and_hides_an_app_and_the_change_is_audited()
    {
        await ResetAsync();
        var app = await CreateAppAsync(_admin, "Pagamentos", isPublic: false);
        Assert.Empty((await GetStatusAsync()).Components);

        var response = await _admin.PutAsJsonAsync(
            $"/api/applications/{app.Id}", new UpdateApplicationRequest(null, null, null, null, IsPublic: true), Json);
        Assert.True((await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!.IsPublic);
        Assert.Single((await GetStatusAsync()).Components);
        Assert.Equal(ComponentStatus.Operational, (await GetStatusAsync()).Overall);

        var audit = await _admin.GetFromJsonAsync<AuditPage>("/api/audit?action=ApplicationUpdated", Json);
        Assert.Contains(audit!.Items, e => e.TargetId == app.Id.ToString() && e.Details!.Contains("IsPublic: False → True"));
    }

    [Fact]
    public async Task Viewers_cannot_publish_apps()
    {
        await ResetAsync();
        var app = await CreateAppAsync(_admin, "Busca", isPublic: false);
        var viewer = new User { Id = Guid.NewGuid(), Email = "viewer-status@example.com", PasswordHash = "x", Role = UserRole.Viewer };
        await factory.WithDbAsync(db => { db.Users.Add(viewer); return db.SaveChangesAsync(); });

        var response = await factory.CreateClientAs(viewer).PutAsJsonAsync(
            $"/api/applications/{app.Id}", new UpdateApplicationRequest(null, null, null, null, IsPublic: true), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

public class CachedPublicStatusApiTests(CachedStatusPageApiFactory factory) : IClassFixture<CachedStatusPageApiFactory>
{
    [Fact]
    public async Task Anonymous_responses_are_cached_so_a_flood_hits_the_database_once()
    {
        var admin = factory.CreateAdminClient();
        using var anonymous = factory.CreateClient();

        var first = await anonymous.GetAsync("/api/public/status");
        Assert.Equal("public, max-age=30", first.Headers.CacheControl?.ToString());

        await PublicStatusApiTests.CreateAppAsync(admin, "Recém-publicada", isPublic: true);

        // Dentro dos 30 s, a resposta vem do cache e ainda não mostra a aplicação nova.
        var second = await anonymous.GetFromJsonAsync<PublicStatusResponse>("/api/public/status", PublicStatusApiTests.Json);
        Assert.Empty(second!.Components);
    }
}

public class StrictPublicStatusApiTests(StrictStatusPageApiFactory factory) : IClassFixture<StrictStatusPageApiFactory>
{
    [Fact]
    public async Task The_public_page_has_its_own_rate_limit()
    {
        using var anonymous = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/public/status")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.GetAsync("/api/public/status")).StatusCode);
    }
}
