using System.Security.Claims;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Auth;

public sealed record CreateUserRequest(string? Email, string? Password, UserRole? Role);

public sealed record UserResponse(
    Guid Id, string Email, UserRole Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? LockedUntil)
{
    // Nunca devolvemos o hash da senha, nem para administradores.
    // LockedUntil: o fim do bloqueio mais longo, seja da conta inteira ou de algum IP nela.
    public static UserResponse From(User u, DateTimeOffset now, DateTimeOffset? ipLockedUntil = null)
    {
        var lockedUntil = u.IsLockedOut(now) ? u.LockoutEnd : null;
        if (ipLockedUntil > now && !(lockedUntil >= ipLockedUntil))
        {
            lockedUntil = ipLockedUntil;
        }

        return new(u.Id, u.Email, u.Role, u.CreatedAt, u.LastLoginAt, lockedUntil);
    }
}

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/users")
            .WithTags("Users")
            .RequireAuthorization(AuthSetup.AdminPolicy);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);
        group.MapDelete("/{id:guid}", DeleteAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);
        group.MapPost("/{id:guid}/unlock", UnlockAsync).RequireRateLimiting(RateLimitingSetup.WritePolicy);

        return api;
    }

    private static async Task<IResult> ListAsync(DeployGuardDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);
        var ipLocks = await db.LoginFailures.AsNoTracking()
            .Where(f => f.LockoutEnd > now)
            .GroupBy(f => f.UserId)
            .Select(g => new { UserId = g.Key, Until = g.Max(f => f.LockoutEnd) })
            .ToDictionaryAsync(x => x.UserId, x => x.Until, ct);
        return Results.Ok(users.Select(u => UserResponse.From(u, now, ipLocks.GetValueOrDefault(u.Id))));
    }

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        HttpContext http,
        DeployGuardDbContext db,
        IPasswordHasher<User> hasher,
        TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!UserEmail.IsValid(request.Email))
        {
            errors["email"] = ["Informe um e-mail válido."];
        }

        var email = UserEmail.Normalize(request.Email ?? "");
        if (PasswordPolicy.Validate(request.Password, email) is { } passwordError)
        {
            errors["password"] = [passwordError];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Já existe um usuário com este e-mail."] });
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Email = email,
            PasswordHash = "",
            Role = request.Role ?? UserRole.Viewer, // menor privilégio por padrão
            CreatedAt = time.GetUtcNow(),
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password!);

        db.Users.Add(user);
        Audit.Record(db, http, AuditAction.UserCreated, "user", user.Id.ToString(), $"{user.Email} ({user.Role})");
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user.Id}", UserResponse.From(user, time.GetUtcNow()));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal principal, HttpContext http, DeployGuardDbContext db, CancellationToken ct)
    {
        if (principal.FindFirstValue(AuthSetup.SubjectClaim) == id.ToString())
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Você não pode remover o próprio usuário.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        db.Users.Remove(user);
        Audit.Record(db, http, AuditAction.UserDeleted, "user", user.Id.ToString(), user.Email);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlockAsync(Guid id, HttpContext http, DeployGuardDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        // Libera também os IPs bloqueados nesta conta.
        await db.LoginFailures.Where(f => f.UserId == user.Id).ExecuteDeleteAsync(ct);
        Audit.Record(db, http, AuditAction.UserUnlocked, "user", user.Id.ToString(), user.Email);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
