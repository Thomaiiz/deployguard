using System.Security.Claims;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Api.Auth;

public sealed record CreateUserRequest(string? Email, string? Password, UserRole? Role);

public sealed record UserResponse(Guid Id, string Email, UserRole Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt)
{
    // Nunca devolvemos o hash da senha, nem para administradores.
    public static UserResponse From(User u) => new(u.Id, u.Email, u.Role, u.CreatedAt, u.LastLoginAt);
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

        return api;
    }

    private static async Task<IResult> ListAsync(DeployGuardDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct)).Select(UserResponse.From));

    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
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
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/users/{user.Id}", UserResponse.From(user));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal principal, DeployGuardDbContext db, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
