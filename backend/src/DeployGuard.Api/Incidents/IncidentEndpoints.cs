using DeployGuard.Api.Data;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Incidents;

public sealed record IncidentResponse(
    long Id,
    Guid ApplicationId,
    string ApplicationName,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    bool IsOpen,
    int DurationSeconds,
    CheckErrorType Cause,
    int? HttpStatusCode,
    string? Summary,
    int NotificationsSent,
    int NotificationsFailed,
    SuspectedDeployment? SuspectedDeployment);

public enum IncidentStatusFilter
{
    All,
    Open,
    Resolved,
}

public static class IncidentEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapIncidentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/incidents", ListAsync).WithTags("Incidents");
        api.MapGet("/applications/{id:guid}/incidents", ListForApplicationAsync).WithTags("Incidents");
        return api;
    }

    private static Task<IResult> ListAsync(
        DeployGuardDbContext db, TimeProvider time, IOptions<DeploymentOptions> deployments, CancellationToken ct,
        IncidentStatusFilter status = IncidentStatusFilter.All, int? limit = null) =>
        QueryAsync(db, time, deployments.Value, null, status, limit, ct);

    private static async Task<IResult> ListForApplicationAsync(
        Guid id, DeployGuardDbContext db, TimeProvider time, IOptions<DeploymentOptions> deployments, CancellationToken ct,
        IncidentStatusFilter status = IncidentStatusFilter.All, int? limit = null)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, ct))
        {
            return Results.NotFound();
        }

        return await QueryAsync(db, time, deployments.Value, id, status, limit, ct);
    }

    private static async Task<IResult> QueryAsync(
        DeployGuardDbContext db, TimeProvider time, DeploymentOptions deployments, Guid? applicationId,
        IncidentStatusFilter status, int? limit, CancellationToken ct)
    {
        var window = TimeSpan.FromMinutes(deployments.CorrelationWindowMinutes);
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var query = db.Incidents.AsNoTracking();

        if (applicationId is not null)
        {
            query = query.Where(i => i.ApplicationId == applicationId);
        }

        query = status switch
        {
            IncidentStatusFilter.Open => query.Where(i => i.ResolvedAt == null),
            IncidentStatusFilter.Resolved => query.Where(i => i.ResolvedAt != null),
            _ => query,
        };

        // Join com applications para trazer o nome em uma única consulta.
        var rows = await query
            .OrderByDescending(i => i.StartedAt)
            .Take(take)
            .Join(db.Applications, i => i.ApplicationId, a => a.Id, (i, a) => new
            {
                Incident = i,
                ApplicationName = a.Name,
                Sent = i.Notifications.Count(n => n.SentAt != null),
                Failed = i.Notifications.Count(n => n.FailedAt != null),
                // Subconsulta correlacionada: o deploy mais recente na janela antes do incidente.
                // Vira um "LATERAL JOIN" no PostgreSQL, ainda numa única ida ao banco.
                Suspect = db.Deployments
                    .Where(d => d.ApplicationId == i.ApplicationId && d.DeployedAt <= i.StartedAt && d.DeployedAt >= i.StartedAt - window)
                    .OrderByDescending(d => d.DeployedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        return Results.Ok(rows.Select(r => new IncidentResponse(
            r.Incident.Id,
            r.Incident.ApplicationId,
            r.ApplicationName,
            r.Incident.StartedAt,
            r.Incident.ResolvedAt,
            r.Incident.ResolvedAt is null,
            (int)((r.Incident.ResolvedAt ?? now) - r.Incident.StartedAt).TotalSeconds,
            r.Incident.Cause,
            r.Incident.HttpStatusCode,
            r.Incident.Summary,
            r.Sent,
            r.Failed,
            SuspectedDeployment.From(r.Suspect, r.Incident.StartedAt))));
    }
}
