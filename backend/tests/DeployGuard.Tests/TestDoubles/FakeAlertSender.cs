using System.Collections.Concurrent;
using DeployGuard.Api.Alerting;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>Registra os alertas em memória em vez de chamar webhooks reais.</summary>
public sealed class FakeAlertSender : IAlertSender
{
    public ConcurrentQueue<(string Channel, AlertMessage Message)> Sent { get; } = new();

    public bool Fail { get; set; }

    public TimeSpan Delay { get; set; }

    public async Task SendAsync(WebhookChannelOptions channel, AlertMessage message, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        if (Fail)
        {
            throw new HttpRequestException("Canal respondeu HTTP 500.", null, System.Net.HttpStatusCode.InternalServerError);
        }

        Sent.Enqueue((channel.Name, message));
    }
}
