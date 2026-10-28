using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.History;

public static class HistoryEndpoints
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;
    public const int MaxStatsWindowHours = 24 * 7;

    public static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/applications/{id:guid}").WithTags("History");

        group.MapGet("/checks", ListChecksAsync);
        group.MapGet("/stats", GetStatsAsync);

        return api;
    }

    private static async Task<IResult> ListChecksAsync(
        Guid id,
        DeployGuardDbContext db,
        CancellationToken ct,
        int? limit = null,
        string? cursor = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CheckStatus? status = null)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, ct))
        {
            return Results.NotFound();
        }

        // Limite de página com teto: o cliente não consegue pedir 1 milhão de linhas.
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = db.HealthCheckResults.AsNoTracking().Where(r => r.ApplicationId == id);

        if (from is not null)
        {
            query = query.Where(r => r.CheckedAt >= from.Value.ToUniversalTime());
        }

        if (to is not null)
        {
            query = query.Where(r => r.CheckedAt <= to.Value.ToUniversalTime());
        }

        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (cursor is not null)
        {
            if (!HistoryCursor.TryDecode(cursor, out var after))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Cursor inválido."] });
            }

            query = query.Where(r => r.CheckedAt < after.CheckedAt || (r.CheckedAt == after.CheckedAt && r.Id < after.Id));
        }

        // Pedimos 1 item a mais só para saber se existe próxima página.
        var rows = await query
            .OrderByDescending(r => r.CheckedAt)
            .ThenByDescending(r => r.Id)
            .Take(pageSize + 1)
            .Select(r => new HealthCheckResultResponse(r.Id, r.CheckedAt, r.Status, r.HttpStatusCode, r.LatencyMs, r.ErrorType, r.ErrorMessage))
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > pageSize)
        {
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = new HistoryCursor(last.CheckedAt, last.Id).Encode();
        }

        return Results.Ok(new HealthCheckPage(rows, nextCursor));
    }

    private static async Task<IResult> GetStatsAsync(
        Guid id,
        DeployGuardDbContext db,
        TimeProvider time,
        CancellationToken ct,
        int? hours = null)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, ct))
        {
            return Results.NotFound();
        }

        var windowHours = Math.Clamp(hours ?? 24, 1, MaxStatsWindowHours);
        var to = time.GetUtcNow();
        var from = to.AddHours(-windowHours);

        // A janela é limitada (máx. 7 dias com intervalo mínimo de 30s ≈ 20 mil linhas
        // de poucos campos), então dá para trazer só o necessário e calcular o p95 em memória.
        var samples = await db.HealthCheckResults.AsNoTracking()
            .Where(r => r.ApplicationId == id && r.CheckedAt >= from && r.CheckedAt <= to)
            .Select(r => new { r.Status, r.LatencyMs, r.ErrorType })
            .ToListAsync(ct);

        if (samples.Count == 0)
        {
            return Results.Ok(new ApplicationStatsResponse(
                id, from, to, 0, null, null, null, null,
                new Dictionary<CheckStatus, int>(), new Dictionary<CheckErrorType, int>()));
        }

        // Latência só faz sentido para respostas recebidas (falhas de rede distorceriam a média).
        var latencies = samples.Where(s => s.Status != CheckStatus.Down).Select(s => s.LatencyMs).Order().ToArray();
        var available = samples.Count(s => s.Status != CheckStatus.Down);

        return Results.Ok(new ApplicationStatsResponse(
            id,
            from,
            to,
            samples.Count,
            Math.Round(100.0 * available / samples.Count, 2),
            latencies.Length > 0 ? Math.Round(latencies.Average(), 1) : null,
            Percentile(latencies, 0.95),
            latencies.Length > 0 ? latencies[^1] : null,
            samples.GroupBy(s => s.Status).ToDictionary(g => g.Key, g => g.Count()),
            samples.Where(s => s.ErrorType != CheckErrorType.None).GroupBy(s => s.ErrorType).ToDictionary(g => g.Key, g => g.Count())));
    }

    /// <summary>Percentil pelo método "nearest rank" sobre um array já ordenado.</summary>
    internal static int? Percentile(int[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(percentile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
