using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const string CookieName = "dg_session";

    /// <summary>
    /// Chave HMAC que assina os tokens. É um SEGREDO: quem tem a chave consegue forjar
    /// tokens de qualquer usuário. Vem só de variável de ambiente / user-secrets.
    /// Mínimo de 32 caracteres (256 bits) para HMAC-SHA256.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string JwtSigningKey { get; set; } = "";

    public string Issuer { get; set; } = "deployguard";

    public string Audience { get; set; } = "deployguard-dashboard";

    /// <summary>Validade do token. Curta, porque JWT não pode ser revogado antes de expirar.</summary>
    [Range(5, 1440)]
    public int TokenLifetimeMinutes { get; set; } = 480;

    /// <summary>Cookie só trafega em HTTPS (navegadores tratam localhost como seguro).</summary>
    public bool CookieSecure { get; set; } = true;

    /// <summary>Cria o primeiro admin na inicialização se o banco não tiver nenhum usuário.</summary>
    public string? BootstrapAdminEmail { get; set; }

    public string? BootstrapAdminPassword { get; set; }
}
