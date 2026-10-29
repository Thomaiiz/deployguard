namespace DeployGuard.Api.Domain;

/// <summary>Uma aplicação cadastrada para ser monitorada por uma URL de health check.</summary>
public sealed class MonitoredApplication
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string HealthCheckUrl { get; set; }

    public int CheckIntervalSeconds { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Aparece na página de status pública (sem login). Desligado por padrão: publicar
    /// é uma decisão explícita do Admin, e só o nome e a situação ficam visíveis.
    /// </summary>
    public bool IsPublic { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Quando o agendador deve verificar esta aplicação de novo.</summary>
    public DateTimeOffset NextCheckAt { get; set; }

    // Campos desnormalizados do último resultado: o dashboard lê o estado atual
    // sem precisar buscar o resultado mais recente de cada aplicação.
    public DateTimeOffset? LastCheckedAt { get; set; }

    public CheckStatus? LastStatus { get; set; }

    public int? LastLatencyMs { get; set; }

    /// <summary>Falhas seguidas; zera na primeira verificação bem-sucedida.</summary>
    public int ConsecutiveFailures { get; set; }

    public List<HealthCheckResult> Results { get; set; } = [];
}
