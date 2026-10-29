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
        var email = UserEmail.Normalize(request.Email ?? "");
        var password = request.Password ?? "";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Mesmo sem usuário, calculamos um hash: assim o tempo de resposta não revela
        // quais e-mails existem (ataque de enumeração por tempo).
        var result = user is null
            ? VerifyAgainstDummy(hasher, password)
            : hasher.VerifyHashedPassword(user, user.PasswordHash, password);

        if (user is null || result == PasswordVerificationResult.Failed)
        {
            // Mensagem genérica: não diz se o erro foi no e-mail ou na senha.
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "E-mail ou senha inválidos.");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // O algoritmo ou o número de iterações ficou mais forte desde o cadastro: atualiza o hash.
            user.PasswordHash = hasher.HashPassword(user, password);
        }

        user.LastLoginAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var (token, expiresAt) = tokens.Create(user);
        http.Response.Cookies.Append(AuthOptions.CookieName, token, CookieOptionsFor(options.Value, expiresAt));

        return Results.Ok(new CurrentUserResponse(user.Id, user.Email, user.Role));
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
