using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Deployments;

public static class DeploymentQueries
{
    /// <summary>O deploy mais recente da aplicação dentro da janela antes do início do incidente.</summary>
    public static Task<Deployment?> SuspectForAsync(
        DeployGuardDbContext db, Guid applicationId, DateTimeOffset incidentStartedAt, TimeSpan window, CancellationToken ct)
    {
        var from = incidentStartedAt - window;
        return db.Deployments.AsNoTracking()
            .Where(d => d.ApplicationId == applicationId && d.DeployedAt <= incidentStartedAt && d.DeployedAt >= from)
            .OrderByDescending(d => d.DeployedAt)
            .FirstOrDefaultAsync(ct);
    }
}
