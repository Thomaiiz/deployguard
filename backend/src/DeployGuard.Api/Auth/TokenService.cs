using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DeployGuard.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DeployGuard.Api.Auth;

/// <summary>
/// Gera o JWT (JSON Web Token): um JSON assinado com quem é o usuário (claims) e até
/// quando vale. O servidor não guarda sessão: valida a assinatura a cada requisição.
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    public (string Token, DateTimeOffset ExpiresAt) Create(User user)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(settings.TokenLifetimeMinutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(AuthSetup.RoleClaim, user.Role.ToString()),
            new Claim(AuthSetup.SecurityStampClaim, user.SecurityStamp.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var credentials = new SigningCredentials(SigningKey(settings), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            settings.Issuer, settings.Audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public static SymmetricSecurityKey SigningKey(AuthOptions settings) =>
        new(Encoding.UTF8.GetBytes(settings.JwtSigningKey));
}
