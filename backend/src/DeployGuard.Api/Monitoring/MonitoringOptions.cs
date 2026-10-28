using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Monitoring;

/// <summary>
/// Configuração do monitoramento (seção "Monitoring" do appsettings / variáveis de ambiente).
/// Validada na inicialização: a aplicação não sobe com valores inseguros ou absurdos.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>
    /// Allowlist de hosts que podem ser cadastrados. Aceita nome exato ("api.exemplo.com"),
    /// curinga de subdomínio ("*.exemplo.com") ou "*" (qualquer host público).
    /// Lista vazia bloqueia tudo.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Hosts explicitamente autorizados a resolver para IPs privados (ex.: um serviço
    /// interno do Docker Compose). Qualquer outro destino privado é bloqueado.
    /// </summary>
    public List<string> AllowedPrivateHosts { get; set; } = [];

    /// <summary>Portas permitidas. Vazio significa apenas 80 e 443.</summary>
    public List<int> AllowedPorts { get; set; } = [];

    /// <summary>Se false, só HTTPS é aceito.</summary>
    public bool AllowHttp { get; set; }

    [Range(1, 30)]
    public int RequestTimeoutSeconds { get; set; } = 5;

    [Range(1, 30)]
    public int ConnectTimeoutSeconds { get; set; } = 3;

    [Range(10, 86_400)]
    public int MinIntervalSeconds { get; set; } = 30;

    [Range(10, 86_400)]
    public int MaxIntervalSeconds { get; set; } = 3_600;

    [Range(10, 86_400)]
    public int DefaultIntervalSeconds { get; set; } = 60;

    [Range(1, 100)]
    public int MaxConcurrentChecks { get; set; } = 10;

    [Range(1, 10_000)]
    public int MaxApplications { get; set; } = 100;

    [Range(1, 60)]
    public int SchedulerTickSeconds { get; set; } = 5;

    [Range(1, 60_000)]
    public int DegradedLatencyThresholdMs { get; set; } = 1_000;

    [Range(1, 365)]
    public int ResultRetentionDays { get; set; } = 30;

    /// <summary>Permite desligar o agendador (usado nos testes de integração).</summary>
    public bool SchedulerEnabled { get; set; } = true;

    public IReadOnlyCollection<int> EffectivePorts => AllowedPorts.Count > 0 ? AllowedPorts : [80, 443];

    public bool IsPrivateHostAllowed(string host) =>
        AllowedPrivateHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
}
