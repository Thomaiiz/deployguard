using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Auth;

public sealed record AuditEventResponse(
    long Id,
    DateTimeOffset At,
    AuditAction Action,
    string ActorEmail,
    string? TargetType,
    string? TargetId,
    string? Details,
    string? IpAddress);

public sealed record AuditPage(IReadOnlyList<AuditEventResponse> Items, long? NextBefore);

public static class AuditEndpoints
{
    public const int MaxPageSize = 200;

    public static RouteGroupBuilder MapAuditEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/audit", ListAsync)
            .WithTags("Audit")
            .RequireAuthorization(AuthSetup.AdminPolicy);
        return api;
    }

    /// <summary>
    /// Mais recentes primeiro, paginado por cursor: "before" é o Id do último item da página
    /// anterior. Só leitura: não existe rota para editar ou apagar registros de auditoria.
    /// </summary>
    private static async Task<IResult> ListAsync(
        DeployGuardDbContext db, int? limit, long? before, AuditAction? action, CancellationToken ct)
    {
        var size = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var query = db.AuditEvents.AsNoTracking();
        if (before is not null)
        {
            query = query.Where(e => e.Id < before);
        }

        if (action is not null)
        {
            query = query.Where(e => e.Action == action);
        }

        // Busca um a mais para saber se existe próxima página.
        var rows = await query.OrderByDescending(e => e.Id).Take(size + 1).ToListAsync(ct);
        var items = rows.Take(size)
            .Select(e => new AuditEventResponse(e.Id, e.At, e.Action, e.ActorEmail, e.TargetType, e.TargetId, e.Details, e.IpAddress))
            .ToList();

        return Results.Ok(new AuditPage(items, rows.Count > size ? items[^1].Id : null));
    }
}
