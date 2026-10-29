using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DeployGuard.Api.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "admin";
    public const string RoleClaim = "role";
    public const string EmailClaim = "email";
    public const string SubjectClaim = "sub";

    public static IServiceCollection AddDeployGuardAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart(); // sem chave de assinatura válida a API não sobe

        services.AddSingleton<TokenService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configurado à parte para poder ler AuthOptions já validadas.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                var settings = auth.Value;
                jwt.MapInboundClaims = false; // mantém "sub", "email", "role" com os nomes do token
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = TokenService.SigningKey(settings),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // impede troca de algoritmo ("alg: none")
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = EmailClaim,
                    RoleClaimType = RoleClaim,
                };

                // O token vem do cookie HttpOnly, não do cabeçalho Authorization.
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        context.Token = context.Request.Cookies[AuthOptions.CookieName];
                        return Task.CompletedTask;
                    },

                    // JWT sozinho não pode ser revogado. Uma consulta barata (busca por chave primária)
                    // garante que um usuário removido ou rebaixado perde o acesso na hora,
                    // em vez de esperar o token expirar.
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal!;
                        if (!Guid.TryParse(principal.FindFirst(SubjectClaim)?.Value, out var userId))
                        {
                            context.Fail("Token sem identificador de usuário.");
                            return;
                        }

                        var db = context.HttpContext.RequestServices.GetRequiredService<DeployGuardDbContext>();
                        var role = await db.Users.AsNoTracking()
                            .Where(u => u.Id == userId)
                            .Select(u => (UserRole?)u.Role)
                            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (role?.ToString() != principal.FindFirst(RoleClaim)?.Value)
                        {
                            context.Fail("Usuário removido ou com papel alterado.");
                        }
                    },
                };
            });

        services.AddAuthorizationBuilder()
            // Política padrão "fechada": todo endpoint exige login, a não ser que diga o contrário.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(nameof(UserRole.Admin)));

        return services;
    }

    /// <summary>Cria o primeiro administrador se ainda não existir nenhum usuário.</summary>
    public static async Task EnsureBootstrapAdminAsync(IServiceProvider services, ILogger logger)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>();
        if (await db.Users.AnyAsync())
        {
            return;
        }

        var settings = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (string.IsNullOrWhiteSpace(settings.BootstrapAdminEmail) || string.IsNullOrEmpty(settings.BootstrapAdminPassword))
        {
            logger.LogWarning("Nenhum usuário cadastrado. Defina Auth__BootstrapAdminEmail e Auth__BootstrapAdminPassword para criar o primeiro admin.");
            return;
        }

        var email = UserEmail.Normalize(settings.BootstrapAdminEmail);
        var error = PasswordPolicy.Validate(settings.BootstrapAdminPassword, email);
        if (error is not null)
        {
            throw new InvalidOperationException($"Senha do admin inicial inválida: {error}");
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            PasswordHash = "",
            Role = UserRole.Admin,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>()
            .HashPassword(user, settings.BootstrapAdminPassword);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        logger.LogInformation("Admin inicial {Email} criado.", email);
    }
}

public static class UserEmail
{
    public const int MaxLength = 254;

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public static bool IsValid(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= MaxLength
        && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var parsed)
        && parsed.Address == email.Trim();
}
