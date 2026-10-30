using System.Security.Claims;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record CurrentUserResponse(Guid Id, string Email, UserRole Role);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingSetup.LoginPolicy);
        group.MapPost("/logout", Logout).AllowAnonymous();
        group.MapGet("/me", Me);
        // Mesmo limite do login: quem roubou uma sessão não pode testar senhas à vontade.
        group.MapPost("/password", ChangePasswordAsync).RequireRateLimiting(RateLimitingSetup.LoginPolicy);

        return api;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        DeployGuardDbContext db,
        IPasswordHasher<User> hasher,
        TokenService tokens,
        IOptions<AuthOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var email = UserEmail.Normalize(request.Email ?? "");
        var password = request.Password ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Mesmo sem usuário (ou com a conta bloqueada), calculamos um hash: assim o tempo de
        // resposta não revela quais e-mails existem (ataque de enumeração por tempo).
        var result = user is null
            ? VerifyAgainstDummy(hasher, password)
            : hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        // Mesma resposta para e-mail inexistente, senha errada e conta bloqueada: quem tenta
        // adivinhar não descobre quais contas existem nem quais estão bloqueadas.
        IResult Invalid() => Results.Problem(statusCode: StatusCodes.Status401Unauthorized,
            title: "E-mail ou senha inválidos.",
            detail: $"Depois de {settings.MaxFailedLogins} tentativas erradas seguidas, a conta fica bloqueada por {settings.LockoutMinutes} minutos.");

        var ip = ClientIp(http);

        if (user is not null)
        {
            // Bloqueio por conta (teto somando todos os IPs) ou só deste IP nesta conta. Durante o
            // bloqueio, nem a senha certa entra, e as tentativas não estendem o bloqueio.
            var lockedFor = user.IsLockedOut(now)
                ? "Conta bloqueada."
                : await db.LoginFailures.AnyAsync(f => f.UserId == user.Id && f.IpAddress == ip && f.LockoutEnd > now, ct)
                    ? "Conta bloqueada para este IP."
                    : null;
            if (lockedFor is not null)
            {
                Audit.Record(db, http, AuditAction.LoginFailed, "user", user.Id.ToString(), lockedFor, actor: user);
                await db.SaveChangesAsync(ct);
                return Invalid();
            }
        }

        if (user is null || result == PasswordVerificationResult.Failed)
        {
            if (user is null)
            {
                Audit.Record(db, http, AuditAction.LoginFailed, details: "E-mail não cadastrado.", actorEmail: email);
            }
            else
            {
                Audit.Record(db, http, AuditAction.LoginFailed, "user", user.Id.ToString(), "Senha incorreta.", actor: user);
                var (ipLocked, accountLocked) = await RegisterFailureAsync(db, user.Id, ip, settings, now, ct);
                if (accountLocked)
                {
                    Audit.Record(db, http, AuditAction.AccountLocked, "user", user.Id.ToString(),
                        $"{settings.MaxFailedLoginsPerAccount} tentativas erradas somando todos os IPs. Conta bloqueada por {settings.LockoutMinutes} min.", actor: user);
                }
                else if (ipLocked)
                {
                    Audit.Record(db, http, AuditAction.AccountLocked, "user", user.Id.ToString(),
                        $"{settings.MaxFailedLogins} tentativas erradas do IP {ip}. Bloqueada para esse IP por {settings.LockoutMinutes} min.", actor: user);
                }
            }

            await db.SaveChangesAsync(ct);
            return Invalid();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // O algoritmo ou o número de iterações ficou mais forte desde o cadastro: atualiza o hash.
            user.PasswordHash = hasher.HashPassword(user, password);
        }

        user.LastLoginAt = now;
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        await db.LoginFailures.Where(f => f.UserId == user.Id && f.IpAddress == ip).ExecuteDeleteAsync(ct);
        Audit.Record(db, http, AuditAction.LoginSucceeded, "user", user.Id.ToString(), actor: user);
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = tokens.Create(user);
        http.Response.Cookies.Append(AuthOptions.CookieName, token, CookieOptionsFor(options.Value, expiresAt));

        return Results.Ok(new CurrentUserResponse(user.Id, user.Email, user.Role));
    }

    /// <summary>
    /// Conta a tentativa errada em dois contadores, cada um num único comando atômico. Com várias
    /// instâncias da API, tentativas simultâneas não "se perdem" (ler, somar e gravar em passos
    /// separados perderia). Ao chegar no limite, bloqueia e zera o contador.
    /// 1. Do par (conta, IP): bloqueia só esse IP. Quem erra de propósito não tranca o dono.
    /// 2. Da conta, somando todos os IPs, com um teto bem mais alto: freia ataques espalhados.
    /// Devolve se cada um dos dois bloqueou agora.
    /// </summary>
    private static async Task<(bool IpLocked, bool AccountLocked)> RegisterFailureAsync(
        DeployGuardDbContext db, Guid userId, string ip, AuthOptions settings, DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset? lockUntil = now.AddMinutes(settings.LockoutMinutes);

        // INSERT ... ON CONFLICT ("upsert"): cria a linha do par na primeira falha e soma nas
        // seguintes, sem corrida entre duas requisições. No SET, "login_failures" é o valor antigo.
        var maxPerIp = settings.MaxFailedLogins;
        var ipLocked = (await db.Database.SqlQuery<bool>($"""
            INSERT INTO login_failures ("UserId", "IpAddress", "FailedCount", "LockoutEnd", "UpdatedAt")
            VALUES ({userId}, {ip}, 1, NULL, {now})
            ON CONFLICT ("UserId", "IpAddress") DO UPDATE SET
                "LockoutEnd" = CASE WHEN login_failures."FailedCount" + 1 >= {maxPerIp} THEN {lockUntil} ELSE login_failures."LockoutEnd" END,
                "FailedCount" = CASE WHEN login_failures."FailedCount" + 1 >= {maxPerIp} THEN 0 ELSE login_failures."FailedCount" + 1 END,
                "UpdatedAt" = {now}
            RETURNING coalesce("LockoutEnd" > {now}, false) AS "Value"
            """).ToListAsync(ct)).Single();

        // No SQL, os dois SET leem o valor ANTIGO de FailedLoginCount.
        var maxPerAccount = settings.MaxFailedLoginsPerAccount;
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockoutEnd, u => u.FailedLoginCount + 1 >= maxPerAccount ? lockUntil : u.LockoutEnd)
            .SetProperty(u => u.FailedLoginCount, u => u.FailedLoginCount + 1 >= maxPerAccount ? 0 : u.FailedLoginCount + 1), ct);
        var accountLocked = await db.Users.Where(u => u.Id == userId).Select(u => u.LockoutEnd > now).FirstAsync(ct);

        return (ipLocked, accountLocked);
    }

    /// <summary>
    /// IP real do cliente: o UseForwardedHeaders já trocou o IP do nginx pelo do X-Forwarded-For,
    /// e só confia nesse cabeçalho quando ele vem do proxy conhecido.
    /// </summary>
    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext http,
        DeployGuardDbContext db,
        IPasswordHasher<User> hasher,
        TokenService tokens,
        IOptions<AuthOptions> options,
        CancellationToken ct)
    {
        var userId = Guid.Parse(http.User.FindFirstValue(AuthSetup.SubjectClaim)!);
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Senha atual incorreta."] });
        }

        if (PasswordPolicy.Validate(request.NewPassword, user.Email) is { } error)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = [error] });
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.NewPassword!) != PasswordVerificationResult.Failed)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = ["A nova senha precisa ser diferente da atual."] });
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword!);
        // Carimbo novo: as sessões abertas em outros navegadores (talvez de quem descobriu a
        // senha antiga) deixam de valer na próxima requisição.
        user.SecurityStamp = Guid.NewGuid();
        Audit.Record(db, http, AuditAction.PasswordChanged, "user", user.Id.ToString(), actor: user);
        await db.SaveChangesAsync(ct);

        // Esta sessão continua: entrega um cookie novo, já com o carimbo novo.
        var (token, expiresAt) = tokens.Create(user);
        http.Response.Cookies.Append(AuthOptions.CookieName, token, CookieOptionsFor(options.Value, expiresAt));
        return Results.NoContent();
    }

    private static IResult Logout(HttpContext http, IOptions<AuthOptions> options)
    {
        // Apaga o cookie. Observação: o JWT em si continua válido até expirar (é stateless);
        // por isso a validade é curta.
        http.Response.Cookies.Delete(AuthOptions.CookieName, CookieOptionsFor(options.Value, null));
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal principal) =>
        Results.Ok(new CurrentUserResponse(
            Guid.Parse(principal.FindFirstValue(AuthSetup.SubjectClaim)!),
            principal.FindFirstValue(AuthSetup.EmailClaim)!,
            Enum.Parse<UserRole>(principal.FindFirstValue(AuthSetup.RoleClaim)!)));

    private static CookieOptions CookieOptionsFor(AuthOptions settings, DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,                 // JavaScript não lê o cookie: um XSS não consegue roubar o token
        Secure = settings.CookieSecure,  // só trafega em HTTPS
        SameSite = SameSiteMode.Strict,  // não é enviado em requisições vindas de outros sites (CSRF)
        Path = "/api",
        Expires = expiresAt,
    };

    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(
        new User { Email = "", PasswordHash = "" }, Guid.NewGuid().ToString());

    private static PasswordVerificationResult VerifyAgainstDummy(IPasswordHasher<User> hasher, string password)
    {
        hasher.VerifyHashedPassword(new User { Email = "", PasswordHash = "" }, DummyHash, password);
        return PasswordVerificationResult.Failed;
    }
}
