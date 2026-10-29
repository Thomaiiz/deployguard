using System.Security.Claims;
using DeployGuard.Api.Data;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Auth;

public static class ApiKeyEndpoints
{
    public const int MaxNameLength = 100;
    public const int MaxKeys = 50;

    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/api-keys")
            .WithTags("ApiKeys")
            .RequireAuthorization(AuthSetup.AdminPolicy);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);

        return api;
    }

    private static async Task<IResult> ListAsync(DeployGuardDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ApiKeys.AsNoTracking().OrderBy(k => k.Name).ToListAsync(ct)).Select(ApiKeyResponse.From));

    private static async Task<IResult> CreateAsync(
        CreateApiKeyRequest request, ClaimsPrincipal principal, DeployGuardDbContext db, TimeProvider time, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"Informe um nome com até {MaxNameLength} caracteres."],
            });
        }

        if (await db.ApiKeys.CountAsync(ct) >= MaxKeys)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"Limite de {MaxKeys} chaves atingido.");
        }

        var key = ApiKeys.Generate();
        var apiKey = new ApiKey
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Name = name,
            Prefix = ApiKeys.DisplayPrefix(key),
            KeyHash = ApiKeys.Hash(key),
            CreatedAt = time.GetUtcNow(),
            CreatedBy = principal.FindFirstValue(AuthSetup.EmailClaim) ?? "desconhecido",
        };

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/api-keys/{apiKey.Id}", new CreatedApiKeyResponse(ApiKeyResponse.From(apiKey), key));
    }

    /// <summary>Revogar = apagar. A próxima chamada com essa chave já recebe 401.</summary>
    private static async Task<IResult> DeleteAsync(Guid id, DeployGuardDbContext db, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
}
