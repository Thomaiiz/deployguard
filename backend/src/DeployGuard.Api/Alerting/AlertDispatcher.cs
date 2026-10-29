using DeployGuard.Api.Data;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Alerting;

/// <summary>
/// Lê a "caixa de saída" (tabela alert_notifications) e entrega as notificações pendentes.
///
/// Entrega "pelo menos uma vez" (at-least-once): se o processo cair entre enviar e
/// gravar SentAt, a mensagem é reenviada. Duplicar um alerta é aceitável; perder, não.
/// Falhas usam backoff exponencial: 30s, 1min, 2min, 4min... até o máximo de tentativas.
///
/// Com várias instâncias da API, cada notificação é reservada por uma instância só
/// (FOR UPDATE SKIP LOCKED, igual ao agendador), então o Slack não recebe o alerta em dobro.
/// </summary>
public sealed class AlertDispatcher(
    IServiceScopeFactory scopeFactory,
    IAlertSender sender,
    IOptionsMonitor<AlertingOptions> options,
    IOptionsMonitor<DeploymentOptions> deploymentOptions,
    TimeProvider time,
    ILogger<AlertDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CurrentValue.DispatcherEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CurrentValue.DispatchIntervalSeconds), time);
        do
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha ao despachar alertas.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> DispatchPendingAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>();

        var now = time.GetUtcNow();
        var pending = await ClaimPendingAsync(db, now, LeaseFor(settings), ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        // Carrega os incidentes das notificações reservadas; o EF liga cada um à sua notificação.
        var incidentIds = pending.Select(n => n.IncidentId).Distinct().ToList();
        await db.Incidents.Where(i => incidentIds.Contains(i.Id)).LoadAsync(ct);

        var applicationIds = pending.Select(n => n.Incident!.ApplicationId).Distinct().ToList();
        var names = await db.Applications
            .Where(a => applicationIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);

        var channels = settings.ActiveChannels.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sent = 0;

        foreach (var notification in pending)
        {
            var incident = notification.Incident!;
            notification.Attempts++;

            if (!channels.TryGetValue(notification.Channel, out var channel))
            {
                notification.FailedAt = now;
                notification.LastError = "Canal não está mais configurado.";
                continue;
            }

            // No alerta de queda, já aponta o deploy suspeito: é a primeira pergunta de quem está de plantão.
            var suspect = notification.EventType == AlertEventType.IncidentOpened
                ? SuspectedDeployment.From(
                    await DeploymentQueries.SuspectForAsync(db, incident.ApplicationId, incident.StartedAt,
                        TimeSpan.FromMinutes(deploymentOptions.CurrentValue.CorrelationWindowMinutes), ct),
                    incident.StartedAt)
                : null;

            var message = new AlertMessage(
                notification.EventType,
                incident.Id,
                incident.ApplicationId,
                names.GetValueOrDefault(incident.ApplicationId, "(aplicação removida)"),
                incident.StartedAt,
                incident.ResolvedAt,
                incident.Cause,
                incident.Summary,
                DashboardLink(settings.DashboardBaseUrl, incident.ApplicationId),
                suspect);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.WebhookTimeoutSeconds));
                await sender.SendAsync(channel, message, timeout.Token);

                notification.SentAt = time.GetUtcNow();
                notification.LastError = null;
                sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                notification.LastError = ex is HttpRequestException { StatusCode: not null }
                    ? ex.Message
                    : ex is OperationCanceledException ? "Timeout ao chamar o webhook." : "Falha de rede ao chamar o webhook.";

                if (notification.Attempts >= settings.MaxDeliveryAttempts)
                {
                    notification.FailedAt = now;
                    logger.LogWarning("Alerta {Id} descartado após {Attempts} tentativas no canal {Channel}.",
                        notification.Id, notification.Attempts, notification.Channel);
                }
                else
                {
                    notification.NextAttemptAt = now + Backoff(notification.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>
    /// Reserva até <see cref="BatchSize"/> notificações vencidas num único UPDATE atômico, empurrando
    /// NextAttemptAt para o fim do prazo. Se a instância cair antes de gravar o resultado, o prazo
    /// vence e outra instância tenta de novo (por isso a entrega é "pelo menos uma vez").
    /// </summary>
    internal static Task<List<AlertNotification>> ClaimPendingAsync(
        DeployGuardDbContext db, DateTimeOffset now, TimeSpan lease, CancellationToken ct)
    {
        var leaseUntil = now + lease;
        return db.AlertNotifications.FromSql($"""
            UPDATE alert_notifications SET "NextAttemptAt" = {leaseUntil}
            WHERE "Id" IN (
                SELECT "Id" FROM alert_notifications
                WHERE "SentAt" IS NULL AND "FailedAt" IS NULL AND "NextAttemptAt" <= {now}
                ORDER BY "NextAttemptAt"
                LIMIT {BatchSize}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).ToListAsync(ct);
    }

    /// <summary>Pior caso do lote: todos os webhooks batendo no timeout, mais uma folga.</summary>
    internal static TimeSpan LeaseFor(AlertingOptions settings) =>
        TimeSpan.FromSeconds(BatchSize * settings.WebhookTimeoutSeconds + 60);

    internal static TimeSpan Backoff(int attempts)
    {
        var delay = BaseBackoff * Math.Pow(2, Math.Max(0, attempts - 1));
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private static string? DashboardLink(string? baseUrl, Guid applicationId) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/applications/{applicationId}";
}
