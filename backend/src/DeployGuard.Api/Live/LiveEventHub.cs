using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Live;

/// <summary>
/// "Central de avisos" em memória (padrão publish/subscribe). Quem produz eventos (agendador,
/// endpoint de deploys) chama <see cref="Publish"/>; cada conexão SSE tem sua própria fila.
///
/// Limitação consciente: vale só dentro desta instância. Com várias instâncias da API, seria
/// preciso um barramento compartilhado (LISTEN/NOTIFY do PostgreSQL ou Redis pub/sub).
/// </summary>
public sealed class LiveEventHub(IOptionsMonitor<LiveOptions> options)
{
    private readonly ConcurrentDictionary<Guid, Channel<LiveEvent>> _subscribers = new();
    private int _count;

    public int SubscriberCount => Volatile.Read(ref _count);

    public bool TrySubscribe(out LiveSubscription? subscription)
    {
        var settings = options.CurrentValue;

        // Reserva a vaga de forma atômica: dois pedidos simultâneos não passam do limite.
        if (Interlocked.Increment(ref _count) > settings.MaxConnections)
        {
            Interlocked.Decrement(ref _count);
            subscription = null;
            return false;
        }

        // Fila limitada que descarta o evento MAIS ANTIGO quando enche: um cliente lento
        // (rede ruim, aba em segundo plano) nunca faz a memória do servidor crescer sem fim.
        var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(settings.BufferPerConnection)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        subscription = new LiveSubscription(channel.Reader, () =>
        {
            if (_subscribers.TryRemove(id, out _))
            {
                Interlocked.Decrement(ref _count);
            }
        });
        return true;
    }

    /// <summary>Entrega o evento a todas as conexões. Nunca bloqueia quem publica.</summary>
    public void Publish(LiveEvent liveEvent)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(liveEvent);
        }
    }
}

public sealed class LiveSubscription(ChannelReader<LiveEvent> reader, Action onDispose) : IDisposable
{
    private int _disposed;

    public ChannelReader<LiveEvent> Reader { get; } = reader;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            onDispose();
        }
    }
}
