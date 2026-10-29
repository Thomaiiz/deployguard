namespace DeployGuard.Api.Domain;

/// <summary>
/// Credencial para máquinas (ex.: o GitHub Actions) registrarem deploys.
/// Guardamos só o hash SHA-256 da chave: quem ler o banco não consegue usá-la.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }

    /// <summary>Ex.: "github-actions-loja".</summary>
    public required string Name { get; set; }

    /// <summary>Primeiros caracteres da chave, só para o Admin reconhecê-la na lista.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 da chave, em hexadecimal.</summary>
    public required string KeyHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
}
