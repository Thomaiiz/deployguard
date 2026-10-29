using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Email;
using DeployGuard.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Auth;

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ForgotPasswordResponse(string Message);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

/// <summary>
/// "Esqueci minha senha" em dois passos:
/// 1. POST /auth/forgot-password: se o e-mail existir, gera um token aleatório e manda um link.
///    A resposta é sempre a mesma, exista o e-mail ou não.
/// 2. POST /auth/reset-password: troca a senha com o token, que vale uma vez só e expira.
/// </summary>
public static class PasswordResetEndpoints
{
    public const string GenericResponse =
        "Se o e-mail estiver cadastrado, você vai receber um link para criar uma nova senha em instantes.";

    public static RouteGroupBuilder MapPasswordResetEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        // Mesmo limite por IP do login: os dois são alvos de robôs.
        group.MapPost("/forgot-password", ForgotAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingSetup.LoginPolicy);
        group.MapPost("/reset-password", ResetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingSetup.LoginPolicy);

        return api;
    }

    /// <summary>32 bytes aleatórios (256 bits) de um gerador criptográfico, em Base64 de URL.</summary>
    internal static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static async Task<IResult> ForgotAsync(
        ForgotPasswordRequest request,
        HttpContext http,
        DeployGuardDbContext db,
        EmailQueue emails,
        IOptionsMonitor<PasswordResetOptions> options,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var logger = loggers.CreateLogger(typeof(PasswordResetEndpoints));
        var now = time.GetUtcNow();
        var email = UserEmail.Normalize(request.Email ?? "");
        var accepted = Results.Accepted(value: new ForgotPasswordResponse(GenericResponse));

        if (!settings.Enabled)
        {
            logger.LogWarning("Pedido de recuperação de senha ignorado: PasswordReset:DashboardBaseUrl não configurado.");
            return accepted;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            // Registrado para o Admin perceber alguém testando e-mails em sequência.
            Audit.Record(db, http, AuditAction.PasswordResetRequested, details: "E-mail não cadastrado.", actorEmail: email);
            await db.SaveChangesAsync(ct);
            return accepted;
        }

        var recent = await db.PasswordResetTokens.CountAsync(t => t.UserId == user.Id && t.CreatedAt > now.AddHours(-1), ct);
        if (recent >= settings.MaxRequestsPerHour)
        {
            Audit.Record(db, http, AuditAction.PasswordResetRequested, "user", user.Id.ToString(),
                $"Limite de {settings.MaxRequestsPerHour} pedidos por hora atingido. Nenhum e-mail enviado.", actor: user);
            await db.SaveChangesAsync(ct);
            return accepted;
        }

        // Só o link mais novo vale: os anteriores ainda pendentes deixam de funcionar.
        await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        // Faxina: pedidos com mais de um dia não servem nem para o limite por hora.
        await db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.CreatedAt < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        var token = NewToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(settings.TokenLifetimeMinutes),
        });
        Audit.Record(db, http, AuditAction.PasswordResetRequested, "user", user.Id.ToString(), "Link enviado por e-mail.", actor: user);
        await db.SaveChangesAsync(ct);

        if (!emails.TryEnqueue(BuildEmail(user.Email, token, settings)))
        {
            logger.LogWarning("Fila de e-mails cheia: pedido de recuperação de senha descartado.");
        }

        return accepted;
    }

    /// <summary>
    /// O token vai no fragmento da URL (depois do #). O navegador nunca envia o fragmento ao
    /// servidor, então ele não aparece no log de acesso do nginx nem em cabeçalhos Referer.
    /// </summary>
    internal static EmailMessage BuildEmail(string to, string token, PasswordResetOptions settings)
    {
        var link = $"{settings.DashboardBaseUrl!.TrimEnd('/')}/reset-password#token={token}";
        return new EmailMessage(to, "DeployGuard: criar uma nova senha", $"""
            Olá,

            Recebemos um pedido para criar uma nova senha para a sua conta no DeployGuard.
            Para continuar, abra o link abaixo. Ele vale por {settings.TokenLifetimeMinutes} minutos e só pode ser usado uma vez:

            {link}

            Se não foi você, ignore este e-mail. A sua senha atual continua valendo.
            """);
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest request,
        HttpContext http,
        DeployGuardDbContext db,
        IPasswordHasher<User> hasher,
        TimeProvider time,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var invalid = Results.Problem(statusCode: StatusCodes.Status400BadRequest,
            title: "Link inválido ou expirado.",
            detail: "Peça um novo link na tela \"Esqueci minha senha\".");

        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 100)
        {
            return invalid;
        }

        var hash = Hash(request.Token);
        var pending = await db.PasswordResetTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .Select(t => new { t.Id, t.UserId })
            .FirstOrDefaultAsync(ct);
        if (pending is null)
        {
            return invalid;
        }

        var user = await db.Users.FirstAsync(u => u.Id == pending.UserId, ct);
        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } error)
        {
            // Senha fraca não gasta o link: a pessoa corrige e tenta de novo.
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [error] });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // "Gasta" o link num UPDATE atômico. Se duas requisições chegarem juntas com o mesmo
        // token, só uma encontra UsedAt nulo e altera 1 linha; a outra altera 0 e é recusada.
        var consumed = await db.PasswordResetTokens
            .Where(t => t.Id == pending.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        if (consumed == 0)
        {
            return invalid;
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword!);
        // Carimbo novo: todas as sessões abertas caem, inclusive a de quem talvez tenha a senha antiga.
        user.SecurityStamp = Guid.NewGuid();
        // Quem provou ter acesso ao e-mail não precisa esperar o fim de um bloqueio.
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        Audit.Record(db, http, AuditAction.PasswordResetCompleted, "user", user.Id.ToString(), actor: user);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }
}
