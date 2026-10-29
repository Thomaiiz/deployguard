using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using DeployGuard.Api.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.StatusPage;

public static class PublicStatusEndpoints
{
    public const string CachePolicy = "public-status";

    public static RouteGroupBuilder MapPublicStatusEndpoints(this RouteGroupBuilder api, IConfiguration configuration)
    {
        var endpoint = api.MapGet("/public/status", GetAsync)
            .WithTags("Public")
            .AllowAnonymous() // a única rota de dados sem login
            .RequireRateLimiting(RateLimitingSetup.PublicPolicy);

        if (configuration.GetValue($"{StatusPageOptions.SectionName}:CacheSeconds", 30) > 0)
        {
            endpoint.CacheOutput(CachePolicy);
        }

        return api;
    }

    private static async Task<IResult> GetAsync(
        HttpContext http,
        DeployGuardDbContext db,
        IOptionsMonitor<StatusPageOptions> statusOptions,
        IOptionsMonitor<MonitoringOptions> monitoringOptions,
        TimeProvider time,
        CancellationToken ct)
    {
        var settings = statusOptions.CurrentValue;
        var days = Math.Min(settings.HistoryDays, monitoringOptions.CurrentValue.ResultRetentionDays);
        var now = time.GetUtcNow();

        // Só o que o Admin publicou, e só as colunas que a página mostra.
        var apps = await db.Applications.AsNoTracking()
            .Where(a => a.IsPublic && a.IsEnabled)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.LastStatus })
            .ToListAsync(ct);

        var ids = apps.Select(a => a.Id).ToArray();
        var openIncidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.ApplicationId) && i.ResolvedAt == null)
            .ToDictionaryAsync(i => i.ApplicationId, i => i.StartedAt, ct);

        // Dias em UTC: hoje é o último, e o primeiro é "days - 1" dias atrás.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var firstDay = today.AddDays(1 - days);
        var daily = await DailyAvailabilityAsync(db, ids, firstDay, ct);

        var components = apps.Select(a =>
        {
            var rows = daily.GetValueOrDefault(a.Id) ?? [];
            var byDay = rows.ToDictionary(r => r.Day);
            var history = Enumerable.Range(0, days)
                .Select(offset => firstDay.AddDays(offset))
                .Select(day => new DailyAvailability(
                    day, byDay.TryGetValue(day, out var row) ? Percent(row.Available, row.Total) : null))
                .ToList();

            var total = rows.Sum(r => r.Total);
            var incidentSince = openIncidents.TryGetValue(a.Id, out var since) ? since : (DateTimeOffset?)null;

            return new PublicComponent(
                a.Name,
                StatusOf(a.LastStatus, incidentSince is not null),
                total > 0 ? Percent(rows.Sum(r => r.Available), total) : null,
                incidentSince,
                history);
        }).ToList();

        var cacheSeconds = settings.CacheSeconds;
        http.Response.Headers.CacheControl = cacheSeconds > 0 ? $"public, max-age={cacheSeconds}" : "no-store";

        return Results.Ok(new PublicStatusResponse(settings.Title, OverallOf(components), now, days, components));
    }

    /// <summary>
    /// Uma falha isolada não é queda: só um incidente aberto (várias falhas seguidas)
    /// mostra "fora do ar". Assim a página pública não pisca vermelho a cada oscilação.
    /// </summary>
    internal static ComponentStatus StatusOf(CheckStatus? lastStatus, bool hasOpenIncident) => (lastStatus, hasOpenIncident) switch
    {
        (_, true) => ComponentStatus.Outage,
        (null, _) => ComponentStatus.Unknown,
        (CheckStatus.Up, _) => ComponentStatus.Operational,
        _ => ComponentStatus.Degraded, // Degraded, ou Down ainda sem incidente
    };

    /// <summary>A situação geral é a pior entre os componentes.</summary>
    internal static ComponentStatus OverallOf(IReadOnlyCollection<PublicComponent> components) =>
        components.Any(c => c.Status == ComponentStatus.Outage) ? ComponentStatus.Outage
        : components.Any(c => c.Status == ComponentStatus.Degraded) ? ComponentStatus.Degraded
        : ComponentStatus.Operational;

    private static double Percent(int available, int total) => Math.Round(100.0 * available / total, 2);

    /// <summary>
    /// Agrupa por aplicação e por dia (UTC) num único GROUP BY. O índice
    /// (ApplicationId, CheckedAt) deixa o banco ler só o intervalo pedido.
    /// </summary>
    private static async Task<Dictionary<Guid, List<DailyRow>>> DailyAvailabilityAsync(
        DeployGuardDbContext db, Guid[] ids, DateOnly firstDay, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var since = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var down = nameof(CheckStatus.Down);
        var rows = await db.Database.SqlQuery<DailyRow>($"""
            SELECT "ApplicationId",
                   ("CheckedAt" AT TIME ZONE 'UTC')::date AS "Day",
                   count(*)::int AS "Total",
                   (count(*) FILTER (WHERE "Status" <> {down}))::int AS "Available"
            FROM health_check_results
            WHERE "ApplicationId" = ANY({ids}) AND "CheckedAt" >= {since}
            GROUP BY 1, 2
            """).ToListAsync(ct);

        return rows.GroupBy(r => r.ApplicationId).ToDictionary(g => g.Key, g => g.ToList());
    }

    internal sealed class DailyRow
    {
        public Guid ApplicationId { get; init; }

        public DateOnly Day { get; init; }

        public int Total { get; init; }

        public int Available { get; init; }
    }
}
