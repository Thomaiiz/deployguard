namespace DeployGuard.Api.Domain;

/// <summary>Resultado consolidado de uma verificação de saúde.</summary>
public enum CheckStatus
{
    /// <summary>Respondeu 2xx dentro do limite de latência.</summary>
    Up,

    /// <summary>Respondeu 2xx, mas acima do limite de latência configurado.</summary>
    Degraded,

    /// <summary>Falhou: timeout, erro de rede, destino bloqueado ou status HTTP fora de 2xx.</summary>
    Down,
}
