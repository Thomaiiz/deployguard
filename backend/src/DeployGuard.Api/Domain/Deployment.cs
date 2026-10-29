namespace DeployGuard.Api.Domain;

/// <summary>
/// Uma versão que foi para produção. Normalmente registrada pelo pipeline de CI/CD
/// (com uma chave de API) logo depois do deploy; também pode ser registrada à mão por um Admin.
/// </summary>
public sealed class Deployment
{
    public long Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Ex.: "v1.4.2" ou "2026.10.29-3".</summary>
    public required string Version { get; set; }

    /// <summary>Hash do commit (7 a 40 caracteres hexadecimais).</summary>
    public string? CommitSha { get; set; }

    public string? Description { get; set; }

    /// <summary>Link para a execução do pipeline. Só https, para nunca virar "javascript:" no dashboard.</summary>
    public string? Url { get; set; }

    public DateTimeOffset DeployedAt { get; set; }

    /// <summary>Quem registrou: e-mail do usuário ou "chave: nome-da-chave".</summary>
    public required string RegisteredBy { get; set; }
}
