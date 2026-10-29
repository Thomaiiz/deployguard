using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Live;

public static class LiveEndpoints
{
    public static RouteGroupBuilder MapLiveEndpoints(this RouteGroupBuilder api)
    {
        // Exige login como o resto da API (política padrão). O EventSource do navegador
        // envia o cookie de sessão sozinho, porque a página e a API têm a mesma origem.
        api.MapGet("/events", Subscribe).WithTags("Live");
        return api;
    }

    private static IResult Subscribe(LiveEventHub hub, IOptions<LiveOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!hub.TrySubscribe(out var subscription))
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Limite de conexões ao vivo atingido. O dashboard continua atualizando a cada minuto.");
        }

        return TypedResults.ServerSentEvents(StreamAsync(subscription!, options.Value, time, ct));
    }

    /// <summary>
    /// Formato SSE na rede (texto simples, uma mensagem por bloco):
    /// <code>
    /// event: check
    /// data: {"type":"CheckCompleted","applicationId":"...","status":"Down",...}
    /// </code>
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<LiveEvent?>> StreamAsync(
        LiveSubscription subscription, LiveOptions settings, TimeProvider time, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = subscription; // libera a vaga quando o cliente desconecta ou o tempo acaba
        var heartbeat = TimeSpan.FromSeconds(settings.HeartbeatSeconds);
        var deadline = time.GetUtcNow().AddMinutes(settings.MaxStreamMinutes);

        // "ready" avisa o cliente que está conectado (ele recarrega tudo, cobrindo o que perdeu
        // enquanto estava desconectado). "retry" diz em quanto tempo reconectar se cair.
        yield return new SseItem<LiveEvent?>(null, "ready") { ReconnectionInterval = TimeSpan.FromSeconds(3) };

        while (!ct.IsCancellationRequested && time.GetUtcNow() < deadline)
        {
            var next = await ReadOrTimeoutAsync(subscription, heartbeat, time, ct);
            yield return next is null
                ? new SseItem<LiveEvent?>(null, "ping")
                : new SseItem<LiveEvent?>(next, next.EventName);
        }
    }

    private static async Task<LiveEvent?> ReadOrTimeoutAsync(
        LiveSubscription subscription, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        // O relógio vem do TimeProvider para os testes poderem "avançar o tempo" sem esperar.
        using var timer = new CancellationTokenSource(timeout, time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            return await subscription.Reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // nenhum evento no intervalo: hora do heartbeat
        }
    }
}
