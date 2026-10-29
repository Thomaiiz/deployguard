using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using DeployGuard.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Auth;

/// <summary>Geração e hash das chaves de API.</summary>
public static class ApiKeys
{
    public const string Prefix = "dg_";

    /// <summary>
    /// 32 bytes aleatórios de um gerador criptográfico (256 bits): impossível de adivinhar,
    /// por isso um SHA-256 simples basta para guardar. Senhas de pessoas, que são fracas,
    /// é que precisam de hash lento como o PBKDF2.
    /// </summary>
    public static string Generate() =>
        Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Parte visível na lista (ex.: "dg_Ab3x"), que não permite reconstruir a chave.</summary>
    public static string DisplayPrefix(string key) => key[..Math.Min(key.Length, Prefix.Length + 4)];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Autentica requisições com "Authorization: Bearer dg_...". Só é consultado pelos endpoints
/// que pedem esse esquema explicitamente (registrar deploy); no resto da API a chave não vale nada.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeployGuardDbContext db,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ScopeClaim = "scope";
    public const string DeploymentsWriteScope = "deployments:write";
    public const string KeyNameClaim = "key_name";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + ApiKeys.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult(); // não é conosco: deixa os outros esquemas tentarem
        }

        var hash = ApiKeys.Hash(header["Bearer ".Length..].Trim());
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash, Context.RequestAborted);
        if (key is null)
        {
            return AuthenticateResult.Fail("Chave de API inválida.");
        }

        // Atualiza "último uso" no máximo uma vez por minuto, para não escrever no banco a cada chamada.
        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            key.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(AuthSetup.SubjectClaim, key.Id.ToString()),
                new Claim(KeyNameClaim, key.Name),
                new Claim(ScopeClaim, DeploymentsWriteScope),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
