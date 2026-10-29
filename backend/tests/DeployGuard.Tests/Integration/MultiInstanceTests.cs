using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Alerting;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Live;
using DeployGuard.Api.Monitoring;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeployGuard.Tests.Integration;

/// <summary>
/// Duas instâncias da API (dois hosts, cada um com seu agendador, dispatcher e central de
/// avisos) usando o MESMO banco, como dois containers atrás do nginx.
/// </summary>
public class MultiInstanceTests : IClassFixture<MultiInstanceApiFactory>, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HealthCheckOutcome Down = new(CheckStatus.Down, 503, 30, CheckErrorType.UnexpectedStatusCode, "HTTP 503");

    private readonly MultiInstanceApiFactory _a;
    private readonly WebApplicationFactory<Program> _b;
    private readonly HttpClient _admin;

    public MultiInstanceTests(MultiInstanceApiFactory factory)
    {
        _a = factory;
        _admin = factory.CreateAdminClient();
        // Herda toda a configuração (inclusive o banco) e sobe um segundo host independente.
        _b = factory.WithWebHostBuilder(_ => { });
        _b.CreateClient().Dispose();
    }

    public void Dispose()
    {
        _a.HealthChecker.Delay = TimeSpan.Zero;
        _a.AlertSender.Delay = TimeSpan.Zero;
        _a.HealthChecker.NextOutcome = new HealthCheckOutcome(CheckStatus.Up, 200, 42, CheckErrorType.None, null);
        _b.Dispose();
    }

    private async Task<List<Guid>> CreateAppsAsync(string prefix, int count)
    {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var response = await _admin.PostAsJsonAsync("/api/applications",
                new CreateApplicationRequest($"{prefix} {i}", $"https://app{i}.example.com/health", 30));
            response.EnsureSuccessStatusCode();
            ids.Add((await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!.Id);
        }

        return ids;
    }

    private async Task DeleteAppsAsync(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            await _admin.DeleteAsync($"/api/applications/{id}");
        }
    }

    private static HealthCheckScheduler SchedulerOf(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<HealthCheckScheduler>();

    private static AlertDispatcher DispatcherOf(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<AlertDispatcher>();

    private static async Task WaitForListenerAsync(WebApplicationFactory<Program> host)
    {
        var listener = host.Services.GetRequiredService<LiveEventListener>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!listener.IsListening)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Two_instances_split_the_work_and_never_check_an_application_twice()
    {
        var ids = await CreateAppsAsync("Divisão", 30);
        await _a.MakeEverythingDueAsync();
        _a.HealthChecker.Delay = TimeSpan.FromMilliseconds(100); // endpoints lentos: as rodadas se sobrepõem

        var counts = await Task.WhenAll(
            SchedulerOf(_a).RunDueChecksAsync(CancellationToken.None),
            SchedulerOf(_b).RunDueChecksAsync(CancellationToken.None));

        Assert.Equal(30, counts.Sum());
        Assert.All(counts, c => Assert.True(c > 0, "as duas instâncias deveriam ter trabalhado"));

        await _a.WithDbAsync(async db =>
        {
            var perApp = await db.HealthCheckResults
                .Where(r => ids.Contains(r.ApplicationId))
                .GroupBy(r => r.ApplicationId)
                .Select(g => g.Count())
                .ToListAsync();
            Assert.Equal(30, perApp.Count);
            Assert.All(perApp, c => Assert.Equal(1, c)); // nenhuma aplicação verificada duas vezes
        });

        await DeleteAppsAsync(ids);
    }

    [Fact]
    public async Task A_claim_hides_the_application_until_its_lease_expires()
    {
        var ids = await CreateAppsAsync("Prazo", 1);
        await _a.MakeEverythingDueAsync();
        var lease = TimeSpan.FromMinutes(2);
        var now = DateTimeOffset.UtcNow;

        await _a.WithDbAsync(async db =>
        {
            var claimed = await HealthCheckScheduler.ClaimDueAsync(db, now, 50, lease, CancellationToken.None);
            Assert.Contains(claimed, a => a.Id == ids[0]);

            // Outra instância, logo depois: a aplicação já está reservada.
            var again = await HealthCheckScheduler.ClaimDueAsync(db, now.AddSeconds(1), 50, lease, CancellationToken.None);
            Assert.DoesNotContain(again, a => a.Id == ids[0]);

            // A instância que reservou "caiu" e nunca gravou o resultado. Vencido o prazo, outra pega.
            var afterLease = await HealthCheckScheduler.ClaimDueAsync(db, now + lease + TimeSpan.FromSeconds(1), 50, lease, CancellationToken.None);
            Assert.Contains(afterLease, a => a.Id == ids[0]);
        });

        await DeleteAppsAsync(ids);
    }

    [Fact]
    public async Task Two_dispatchers_deliver_each_alert_exactly_once()
    {
        var ids = await CreateAppsAsync("Alerta", 12);
        _a.HealthChecker.NextOutcome = Down;
        for (var round = 0; round < 2; round++) // limiar de 2 falhas: abre os incidentes
        {
            await _a.MakeEverythingDueAsync();
            await SchedulerOf(_a).RunDueChecksAsync(CancellationToken.None);
        }

        _a.AlertSender.Fail = false;
        _a.AlertSender.Delay = TimeSpan.FromMilliseconds(50);
        _a.AlertSender.Sent.Clear();

        await Task.WhenAll(
            DispatcherOf(_a).DispatchPendingAsync(CancellationToken.None),
            DispatcherOf(_b).DispatchPendingAsync(CancellationToken.None));

        var opened = _a.AlertSender.Sent
            .Where(s => s.Message.EventType == AlertEventType.IncidentOpened && ids.Contains(s.Message.ApplicationId))
            .ToList();
        Assert.Equal(12, opened.Count);
        Assert.Equal(12, opened.Select(s => s.Message.IncidentId).Distinct().Count()); // ninguém recebeu em dobro

        await DeleteAppsAsync(ids);
    }

    [Fact]
    public async Task Live_events_reach_browsers_connected_to_the_other_instance()
    {
        var ids = await CreateAppsAsync("Ao vivo", 1);
        await WaitForListenerAsync(_b);

        // Navegador conectado na instância B.
        using var browser = _b.CreateClient();
        using (var scope = _a.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>().Users.Single(u => u.Email == ApiFactory.AdminEmail);
            var (token, _) = _a.Services.GetRequiredService<TokenService>().Create(admin);
            browser.DefaultRequestHeaders.Add("Cookie", $"{AuthOptions.CookieName}={token}");
        }

        using var response = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/events"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        // A verificação roda na instância A.
        await _a.MakeEverythingDueAsync();
        await SchedulerOf(_a).RunDueChecksAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        var found = false;
        while (!found && (line = await reader.ReadLineAsync(timeout.Token)) is not null)
        {
            found = line.StartsWith("data: ") && line.Contains(ids[0].ToString());
        }

        Assert.True(found, "o aviso da instância A deveria chegar ao navegador conectado na B");
        await DeleteAppsAsync(ids);
    }

    [Fact]
    public async Task Notifications_from_a_rolled_back_transaction_are_never_delivered()
    {
        await WaitForListenerAsync(_a);
        var hub = _a.Services.GetRequiredService<LiveEventHub>();
        var bus = _a.Services.GetRequiredService<LiveEventBus>();
        Assert.True(hub.TrySubscribe(out var subscription));
        using var _ = subscription;

        var discarded = new LiveEvent(LiveEventType.DeploymentRegistered, Guid.NewGuid(), DateTimeOffset.UtcNow, Version: "desfeito");
        var committed = new LiveEvent(LiveEventType.DeploymentRegistered, Guid.NewGuid(), DateTimeOffset.UtcNow, Version: "confirmado");

        await _a.WithDbAsync(async db =>
        {
            await using (var rollback = await db.Database.BeginTransactionAsync())
            {
                await bus.PublishAsync(db, [discarded], CancellationToken.None);
                await rollback.RollbackAsync();
            }

            await using var commit = await db.Database.BeginTransactionAsync();
            await bus.PublishAsync(db, [committed], CancellationToken.None);
            await commit.CommitAsync();
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await subscription!.Reader.ReadAsync(timeout.Token);
        Assert.Equal("confirmado", received.Version);
    }

    [Fact]
    public async Task Instances_starting_together_apply_migrations_only_once()
    {
        var connection = new NpgsqlConnectionStringBuilder(_a.ConnectionString)
        {
            Database = $"deployguard_boot_{Guid.NewGuid():N}",
        }.ConnectionString;

        DeployGuardDbContext NewContext() =>
            new(new DbContextOptionsBuilder<DeployGuardDbContext>().UseNpgsql(connection).Options);

        await using var first = NewContext();
        await using var second = NewContext();
        try
        {
            // Sem a trava, uma das duas quebraria com "column already exists".
            await Task.WhenAll(DatabaseStartup.MigrateAsync(first), DatabaseStartup.MigrateAsync(second));

            await using var check = NewContext();
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            await first.Database.EnsureDeletedAsync();
        }
    }
}
