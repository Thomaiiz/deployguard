using System.Text;
using System.Text.Json;

namespace DeployGuard.Api.Alerting;

public interface IAlertSender
{
    /// <summary>Envia a mensagem; lança exceção se o canal não aceitar.</summary>
    Task SendAsync(WebhookChannelOptions channel, AlertMessage message, CancellationToken ct);
}

public sealed class WebhookAlertSender(IHttpClientFactory httpClientFactory) : IAlertSender
{
    public const string HttpClientName = "alert-webhooks";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(WebhookChannelOptions channel, AlertMessage message, CancellationToken ct)
    {
        // Cada serviço espera um formato de JSON diferente.
        object payload = channel.Format switch
        {
            WebhookFormat.Slack => new { text = message.Text() },
            WebhookFormat.Discord => new { content = message.Text() },
            _ => new
            {
                @event = message.EventType.ToString(),
                incidentId = message.IncidentId,
                applicationId = message.ApplicationId,
                applicationName = message.ApplicationName,
                startedAt = message.StartedAt,
                resolvedAt = message.ResolvedAt,
                cause = message.Cause.ToString(),
                summary = message.Summary,
                dashboardUrl = message.DashboardUrl,
                text = message.Text(),
            },
        };

        // Serializamos antes de enviar para mandar Content-Length em vez de "chunked":
        // alguns receptores de webhook simples não aceitam transferência em partes.
        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.PostAsync(channel.Url, content, ct);

        // Não incluímos a URL na exceção: ela é um segredo e acabaria em logs.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Canal '{channel.Name}' respondeu HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }
}
