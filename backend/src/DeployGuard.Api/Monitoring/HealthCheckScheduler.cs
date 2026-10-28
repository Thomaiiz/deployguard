using System.Collections.Concurrent;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Monitoring;

/// <summary>
/// Serviço em segundo plano (BackgroundService) que roda dentro do próprio processo da API.
/// A cada "tick" busca as aplicações com verificação vencida, executa as verificações em
/// paralelo (com limite de concorrência) e grava os resultados.
/// </summary>
public sealed class HealthCheckScheduler(
    IServiceScopeFactory scopeFactory,
    IHealthChecker checker,
    IOptionsMonitor<MonitoringOptions> options,
    TimeProvider time,
    ILogger<HealthCheckScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);
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

    /// <summary>Executa uma rodada. Público para ser chamado diretamente nos testes.</summary>
    public async Task<int> RunDueChecksAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;

        // DbContext é "scoped" e não é thread-safe; o BackgroundService é singleton,
        // então criamos um escopo por rodada.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>();

        var now = time.GetUtcNow();
        var dueApps = await db.Applications
            .Where(a => a.IsEnabled && a.NextCheckAt <= now)
            .OrderBy(a => a.NextCheckAt)
            .Take(settings.MaxApplications)
            .ToListAsync(ct);

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

        // De volta a uma única thread: atualiza estado atual e grava o histórico numa só transação.
        foreach (var app in dueApps)
        {
            var (outcome, checkedAt) = outcomes[app.Id];

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
        }

        await db.SaveChangesAsync(ct);
        logger.LogDebug("{Count} health checks executados.", dueApps.Count);
        return dueApps.Count;
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
    }
}
