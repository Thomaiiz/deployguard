using System.Security.Claims;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;

namespace DeployGuard.Api.Auth;

public static class Audit
{
    /// <summary>
    /// Adiciona um registro ao DbContext. Ele é gravado no mesmo SaveChanges da ação auditada:
    /// ou os dois entram no banco, ou nenhum.
    /// </summary>
    public static void Record(
        DeployGuardDbContext db,
        HttpContext http,
        AuditAction action,
        string? targetType = null,
        string? targetId = null,
        string? details = null,
        User? actor = null,
        string? actorEmail = null)
    {
        Guid? actorId = actor?.Id;
        if (actorId is null && Guid.TryParse(http.User.FindFirstValue(AuthSetup.SubjectClaim), out var fromToken))
        {
            actorId = fromToken;
        }

        db.AuditEvents.Add(new AuditEvent
        {
            At = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow(),
            Action = action,
            ActorId = actorId,
            ActorEmail = Truncate(actor?.Email ?? actorEmail ?? http.User.FindFirstValue(AuthSetup.EmailClaim) ?? "(anônimo)", 254),
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? null : Truncate(details, 500),
            // Já é o IP real do cliente: o UseForwardedHeaders confia só no X-Forwarded-For do nginx.
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
