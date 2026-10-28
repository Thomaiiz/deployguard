using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Alerting;

public enum WebhookFormat
{
    /// <summary>JSON com todos os campos do evento, para integrações próprias.</summary>
    Generic,
    Slack,
    Discord,
}

public sealed class WebhookChannelOptions
{
    public string Name { get; set; } = "";

    /// <summary>
    /// URL do webhook. É um SEGREDO (no Slack/Discord, quem tem a URL pode postar no canal):
    /// configure só por variável de ambiente, nunca no appsettings versionado.
    /// Canais com URL vazia são ignorados.
    /// </summary>
    public string Url { get; set; } = "";

    public WebhookFormat Format { get; set; } = WebhookFormat.Generic;
}

public sealed class AlertingOptions
{
    public const string SectionName = "Alerting";

    /// <summary>
    /// Falhas consecutivas necessárias para abrir um incidente. Com 1, qualquer
    /// instabilidade momentânea viraria alerta ("flapping"); 2 ou 3 é o usual.
    /// </summary>
    [Range(1, 10)]
    public int FailureThreshold { get; set; } = 2;

    public List<WebhookChannelOptions> Webhooks { get; set; } = [];

    [Range(1, 20)]
    public int MaxDeliveryAttempts { get; set; } = 6;

    [Range(1, 300)]
    public int DispatchIntervalSeconds { get; set; } = 10;

    [Range(1, 30)]
    public int WebhookTimeoutSeconds { get; set; } = 10;

    /// <summary>Endereço do dashboard, usado para colocar um link na mensagem (opcional).</summary>
    public string? DashboardBaseUrl { get; set; }

    /// <summary>Permite webhooks em http:// (só para desenvolvimento local).</summary>
    public bool AllowInsecureWebhooks { get; set; }

    public bool DispatcherEnabled { get; set; } = true;

    public IReadOnlyList<WebhookChannelOptions> ActiveChannels =>
        Webhooks.Where(w => !string.IsNullOrWhiteSpace(w.Url)).ToList();
}
