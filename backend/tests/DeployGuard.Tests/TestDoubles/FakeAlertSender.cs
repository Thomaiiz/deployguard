using System.Collections.Concurrent;
using DeployGuard.Api.Alerting;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>Registra os alertas em memória em vez de chamar webhooks reais.</summary>
public sealed class FakeAlertSender : IAlertSender
{
    public ConcurrentQueue<(string Channel, AlertMessage Message)> Sent { get; } = new();

    public bool Fail { get; set; }

    public Task SendAsync(WebhookChannelOptions channel, AlertMessage message, CancellationToken ct)
    {
        if (Fail)
        {
            throw new HttpRequestException("Canal respondeu HTTP 500.", null, System.Net.HttpStatusCode.InternalServerError);
        }

        Sent.Enqueue((channel.Name, message));
        return Task.CompletedTask;
    }
}
