using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Applications;

internal static class AvailabilityQueries
{
    /// <summary>
    /// Percentual de verificações "disponíveis" (Up ou Degraded) nas últimas 24h,
    /// calculado no banco com um único GROUP BY.
    /// </summary>
    public static async Task<Dictionary<Guid, double?>> Last24hByApplicationAsync(
        DeployGuardDbContext db, DateTimeOffset now, CancellationToken ct, Guid? applicationId = null)
    {
        var since = now.AddHours(-24);
        var query = db.HealthCheckResults.AsNoTracking().Where(r => r.CheckedAt >= since);

        if (applicationId is not null)
        {
            query = query.Where(r => r.ApplicationId == applicationId);
        }

        var rows = await query
            .GroupBy(r => r.ApplicationId)
            .Select(g => new
            {
                ApplicationId = g.Key,
                Total = g.Count(),
                Available = g.Count(r => r.Status != CheckStatus.Down),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.ApplicationId,
            r => (double?)Math.Round(100.0 * r.Available / r.Total, 2));
    }
}
