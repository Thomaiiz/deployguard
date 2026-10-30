using System.Collections.Concurrent;
using DeployGuard.Api.Alerting;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Monitoring;

/// <summary>
/// Serviço em segundo plano (BackgroundService) que roda dentro do próprio processo da API.
/// A cada "tick" reserva um lote de aplicações com verificação vencida, executa as
/// verificações em paralelo (com limite de concorrência) e grava os resultados.
///
/// Pode haver várias instâncias da API rodando ao mesmo tempo: a reserva com
/// FOR UPDATE SKIP LOCKED garante que cada aplicação é verificada por uma instância só.
/// </summary>
public sealed class HealthCheckScheduler(
    IServiceScopeFactory scopeFactory,
    IHealthChecker checker,
    IncidentTracker incidentTracker,
    IOptionsMonitor<MonitoringOptions> options,
    IOptionsMonitor<AlertingOptions> alertingOptions,
    LiveEventBus liveEvents,
    TimeProvider time,
    ILogger<HealthCheckScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);

    /// <summary>Lote reservado por vez: o bastante para 4 "ondas" de verificações paralelas.</summary>
    private const int WavesPerBatch = 4;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.SchedulerEnabled)
        {
            logger.LogInformation("Agendador de health checks desabilitado por configuração.");
            return;
        }

        // PeriodicTimer não acumula ticks: se uma rodada demorar, a próxima espera terminar.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CurrentValue.SchedulerTickSeconds), time);
        do
        {
            try
            {
                await RunDueChecksAsync(stoppingToken);
                await CleanupOldResultsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Uma falha (ex.: banco fora do ar) não pode derrubar o serviço; tentamos no próximo tick.
                logger.LogError(ex, "Falha na rodada de health checks.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Executa uma rodada: processa lotes até não sobrar aplicação vencida.
    /// Público para ser chamado diretamente nos testes.
    /// </summary>
    public async Task<int> RunDueChecksAsync(CancellationToken ct)
    {
        var total = 0;
        int processed;
        do
        {
            processed = await RunBatchAsync(ct);
            total += processed;
        }
        while (processed > 0 && !ct.IsCancellationRequested);

        if (total > 0)
        {
            logger.LogInformation("{Count} health checks executados nesta instância.", total);
        }

        return total;
    }

    /// <summary>
    /// Reserva aplicações vencidas de forma atômica, num único UPDATE:
    /// <list type="bullet">
    /// <item>FOR UPDATE SKIP LOCKED: se outra instância está reservando as mesmas linhas
    /// neste instante, elas são puladas em vez de esperar. Cada linha vai para uma instância só.</item>
    /// <item>A reserva empurra NextCheckAt para o fim de um "prazo" (lease). Para as outras
    /// instâncias, a aplicação deixa de estar vencida. Ao terminar, gravamos o NextCheckAt certo.</item>
    /// <item>Se a instância cair no meio, o prazo vence e outra instância pega a aplicação.
    /// Nada fica preso para sempre, e nenhuma trava fica aberta durante as chamadas HTTP.</item>
    /// </list>
    /// </summary>
    internal static Task<List<MonitoredApplication>> ClaimDueAsync(
        DeployGuardDbContext db, DateTimeOffset now, int limit, TimeSpan lease, CancellationToken ct)
    {
        var leaseUntil = now + lease;
        return db.Applications.FromSql($"""
            UPDATE applications SET "NextCheckAt" = {leaseUntil}
            WHERE "Id" IN (
                SELECT "Id" FROM applications
                WHERE "IsEnabled" AND "NextCheckAt" <= {now}
                ORDER BY "NextCheckAt"
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Prazo da reserva: o pior caso de um lote (todas as ondas batendo no timeout), em dobro,
    /// mais uma folga. Calculado a partir da configuração para não haver um número a mais para errar.
    /// </summary>
    internal static TimeSpan LeaseFor(MonitoringOptions settings) =>
        TimeSpan.FromSeconds(WavesPerBatch * settings.RequestTimeoutSeconds * 2 + 60);

    private async Task<int> RunBatchAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;

        // DbContext é "scoped" e não é thread-safe; o BackgroundService é singleton,
        // então criamos um escopo por lote.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>();

        var dueApps = await ClaimDueAsync(db, time.GetUtcNow(),
            settings.MaxConcurrentChecks * WavesPerBatch, LeaseFor(settings), ct);

        if (dueApps.Count == 0)
        {
            return 0;
        }

        // As verificações rodam em paralelo, mas SEM tocar no DbContext.
        var outcomes = new ConcurrentDictionary<Guid, (HealthCheckOutcome Outcome, DateTimeOffset CheckedAt)>();
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = settings.MaxConcurrentChecks, CancellationToken = ct };

        await Parallel.ForEachAsync(dueApps, parallelOptions, async (app, token) =>
        {
            var checkedAt = time.GetUtcNow();
            var outcome = await checker.CheckAsync(app.HealthCheckUrl, token);
            outcomes[app.Id] = (outcome, checkedAt);
        });

        var dueIds = dueApps.Select(a => a.Id).ToList();
        var openIncidents = await db.Incidents
            .Where(i => dueIds.Contains(i.ApplicationId) && i.ResolvedAt == null)
            .ToDictionaryAsync(i => i.ApplicationId, ct);
        var channels = alertingOptions.CurrentValue.ActiveChannels;

        // De volta a uma única thread: atualiza estado atual, histórico, incidentes e a
        // caixa de saída de alertas numa só transação (tudo ou nada).
        var events = new List<LiveEvent>(dueApps.Count);
        foreach (var app in dueApps)
        {
            var (outcome, checkedAt) = outcomes[app.Id];
            openIncidents.TryGetValue(app.Id, out var openIncident);

            db.HealthCheckResults.Add(new HealthCheckResult
            {
                ApplicationId = app.Id,
                CheckedAt = checkedAt,
                Status = outcome.Status,
                HttpStatusCode = outcome.HttpStatusCode,
                LatencyMs = outcome.LatencyMs,
                ErrorType = outcome.ErrorType,
                ErrorMessage = outcome.ErrorMessage,
            });

            app.LastCheckedAt = checkedAt;
            app.LastStatus = outcome.Status;
            app.LastLatencyMs = outcome.LatencyMs;
            app.NextCheckAt = checkedAt.AddSeconds(app.CheckIntervalSeconds);

            var (failures, transition) = incidentTracker.Evaluate(app.ConsecutiveFailures, openIncident is not null, outcome);
            app.ConsecutiveFailures = failures;
            events.Add(new LiveEvent(LiveEventType.CheckCompleted, app.Id, checkedAt, outcome.Status, outcome.LatencyMs));

            switch (transition)
            {
                case IncidentTransition.Open:
                    var incident = new Incident
                    {
                        ApplicationId = app.Id,
                        StartedAt = checkedAt,
                        Cause = outcome.ErrorType,
                        HttpStatusCode = outcome.HttpStatusCode,
                        Summary = outcome.ErrorMessage,
                    };
                    db.Incidents.Add(incident);
                    Enqueue(incident, AlertEventType.IncidentOpened, channels, checkedAt);
                    events.Add(new LiveEvent(LiveEventType.IncidentOpened, app.Id, checkedAt, outcome.Status));
                    logger.LogWarning("Incidente aberto para {Application} ({Cause}).", app.Name, outcome.ErrorType);
                    break;

                case IncidentTransition.Resolve:
                    openIncident!.ResolvedAt = checkedAt;
                    Enqueue(openIncident, AlertEventType.IncidentResolved, channels, checkedAt);
                    events.Add(new LiveEvent(LiveEventType.IncidentResolved, app.Id, checkedAt, outcome.Status));
                    logger.LogInformation("Incidente {Id} resolvido para {Application}.", openIncident.Id, app.Name);
                    break;
            }
        }

        // Os avisos ao vivo vão na MESMA transação: o PostgreSQL só entrega um NOTIFY quando a
        // transação é confirmada. Quem receber o aviso e consultar a API já encontra os dados.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await liveEvents.PublishAsync(db, events, ct);
        await transaction.CommitAsync(ct);
        return dueApps.Count;
    }

    private static void Enqueue(Incident incident, AlertEventType eventType, IReadOnlyList<WebhookChannelOptions> channels, DateTimeOffset now)
    {
        // Uma linha por canal: cada canal tem suas próprias tentativas e falhas.
        foreach (var channel in channels)
        {
            incident.Notifications.Add(new AlertNotification
            {
                EventType = eventType,
                Channel = channel.Name,
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }
    }

    private async Task CleanupOldResultsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (now - _lastCleanup < CleanupEvery)
        {
            return;
        }

        _lastCleanup = now;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>();

        var cutoff = now.AddDays(-options.CurrentValue.ResultRetentionDays);
        var removed = await db.HealthCheckResults.Where(r => r.CheckedAt < cutoff).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            logger.LogInformation("Retenção: {Count} resultados antigos removidos.", removed);
        }

        // Tentativas erradas de login com mais de um dia já não bloqueiam nada.
        await db.LoginFailures.Where(f => f.UpdatedAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
    }
}
