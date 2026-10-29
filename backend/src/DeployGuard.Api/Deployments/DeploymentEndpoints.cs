using System.Security.Claims;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Deployments;

public static class DeploymentEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapDeploymentEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/deployments", ListAllAsync).WithTags("Deployments");
        api.MapGet("/applications/{id:guid}/deployments", ListForApplicationAsync).WithTags("Deployments");
        api.MapPost("/applications/{id:guid}/deployments", CreateAsync)
            .WithTags("Deployments")
            .RequireAuthorization(AuthSetup.DeploymentWriterPolicy)
            .RequireRateLimiting(RateLimitingSetup.WritePolicy);

        return api;
    }

    private static Task<IResult> ListAllAsync(DeployGuardDbContext db, CancellationToken ct, int? limit = null) =>
        QueryAsync(db, null, limit, ct);

    private static async Task<IResult> ListForApplicationAsync(Guid id, DeployGuardDbContext db, CancellationToken ct, int? limit = null)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == id, ct))
        {
            return Results.NotFound();
        }

        return await QueryAsync(db, id, limit, ct);
    }

    private static async Task<IResult> QueryAsync(DeployGuardDbContext db, Guid? applicationId, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var query = db.Deployments.AsNoTracking();
        if (applicationId is not null)
        {
            query = query.Where(d => d.ApplicationId == applicationId);
        }

        var rows = await query
            .OrderByDescending(d => d.DeployedAt)
            .Take(take)
            .Join(db.Applications, d => d.ApplicationId, a => a.Id, (d, a) => new { Deployment = d, ApplicationName = a.Name })
            .ToListAsync(ct);

        return Results.Ok(rows.Select(r => ToResponse(r.Deployment, r.ApplicationName)));
    }

    private static async Task<IResult> CreateAsync(
        Guid id,
        CreateDeploymentRequest request,
        ClaimsPrincipal principal,
        DeployGuardDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return Results.NotFound();
        }

        var now = time.GetUtcNow();
        var errors = DeploymentValidator.Validate(request, now);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var deployment = new Deployment
        {
            ApplicationId = id,
            Version = request.Version!.Trim(),
            CommitSha = DeploymentValidator.Clean(request.CommitSha)?.ToLowerInvariant(),
            Description = DeploymentValidator.Clean(request.Description),
            Url = DeploymentValidator.Clean(request.Url),
            DeployedAt = request.DeployedAt ?? now,
            RegisteredBy = RegisteredBy(principal),
        };

        db.Deployments.Add(deployment);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/applications/{id}/deployments", ToResponse(deployment, app.Name));
    }

    /// <summary>Auditoria: guarda quem registrou, pessoa ou chave de API.</summary>
    private static string RegisteredBy(ClaimsPrincipal principal) =>
        principal.FindFirstValue(ApiKeyAuthenticationHandler.KeyNameClaim) is { } keyName
            ? $"chave: {keyName}"
            : principal.FindFirstValue(AuthSetup.EmailClaim) ?? "desconhecido";

    private static DeploymentResponse ToResponse(Deployment d, string applicationName) => new(
        d.Id, d.ApplicationId, applicationName, d.Version, d.CommitSha, d.Description, d.Url, d.DeployedAt, d.RegisteredBy);
}
