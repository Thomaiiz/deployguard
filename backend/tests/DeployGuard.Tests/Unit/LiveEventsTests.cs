using System.Net.ServerSentEvents;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Live;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DeployGuard.Tests.Unit;

public class LiveEventsTests
{
    private sealed class Options(LiveOptions value) : IOptionsMonitor<LiveOptions>
    {
        public LiveOptions CurrentValue => value;
        public LiveOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<LiveOptions, string?> listener) => null;
    }

    private static LiveEventHub Hub(int maxConnections = 10, int buffer = 10) =>
        new(new Options(new LiveOptions { MaxConnections = maxConnections, BufferPerConnection = buffer }));

    private static LiveEvent Check(Guid? appId = null) =>
        new(LiveEventType.CheckCompleted, appId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, CheckStatus.Up, 42);

    [Fact]
    public void Publish_reaches_every_subscriber()
    {
        var hub = Hub();
        Assert.True(hub.TrySubscribe(out var first));
        Assert.True(hub.TrySubscribe(out var second));

        var sent = Check();
        hub.Publish(sent);

        Assert.True(first!.Reader.TryRead(out var a));
        Assert.True(second!.Reader.TryRead(out var b));
        Assert.Same(sent, a);
        Assert.Same(sent, b);
    }

    [Fact]
    public void Connection_limit_is_enforced_and_dispose_frees_the_slot()
    {
        var hub = Hub(maxConnections: 2);
        Assert.True(hub.TrySubscribe(out var first));
        Assert.True(hub.TrySubscribe(out _));
        Assert.False(hub.TrySubscribe(out var refused));
        Assert.Null(refused);
        Assert.Equal(2, hub.SubscriberCount);

        first!.Dispose();
        first.Dispose(); // dispose duplo não libera duas vagas
        Assert.Equal(1, hub.SubscriberCount);
        Assert.True(hub.TrySubscribe(out _));
        Assert.False(hub.TrySubscribe(out _));
    }

    [Fact]
    public void Slow_subscriber_keeps_only_the_newest_events()
    {
        var hub = Hub(buffer: 10);
        Assert.True(hub.TrySubscribe(out var slow));

        var events = Enumerable.Range(0, 25).Select(_ => Check()).ToList();
        events.ForEach(hub.Publish);

        var received = new List<LiveEvent>();
        while (slow!.Reader.TryRead(out var item))
        {
            received.Add(item);
        }

        Assert.Equal(events.TakeLast(10), received);
    }

    [Fact]
    public void Disposed_subscriber_stops_receiving()
    {
        var hub = Hub();
        Assert.True(hub.TrySubscribe(out var subscription));
        subscription!.Dispose();

        hub.Publish(Check());
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(LiveEventType.CheckCompleted, "check")]
    [InlineData(LiveEventType.IncidentOpened, "incident-opened")]
    [InlineData(LiveEventType.IncidentResolved, "incident-resolved")]
    [InlineData(LiveEventType.DeploymentRegistered, "deployment")]
    public void Event_names_match_the_frontend_contract(LiveEventType type, string expected) =>
        Assert.Equal(expected, new LiveEvent(type, Guid.NewGuid(), DateTimeOffset.UtcNow).EventName);

    [Fact]
    public async Task Stream_sends_ready_events_pings_and_ends_at_the_deadline()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-29T12:00:00Z"));
        var settings = new LiveOptions { HeartbeatSeconds = 15, MaxStreamMinutes = 1 };
        var hub = Hub();
        Assert.True(hub.TrySubscribe(out var subscription));

        await using var stream = LiveEndpoints.StreamAsync(subscription!, settings, time, CancellationToken.None)
            .GetAsyncEnumerator();

        // 1) "ready" logo ao conectar, com o intervalo de reconexão.
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("ready", stream.Current.EventType);
        Assert.Equal(TimeSpan.FromSeconds(3), stream.Current.ReconnectionInterval);

        // 2) Um evento publicado sai com o nome certo.
        var sent = Check();
        hub.Publish(sent);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("check", stream.Current.EventType);
        Assert.Same(sent, stream.Current.Data);

        // 3) Sem eventos, passado o heartbeat, sai um "ping".
        var waiting = stream.MoveNextAsync().AsTask();
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await waiting);
        Assert.Equal("ping", stream.Current.EventType);

        // 4) Depois do tempo máximo, a conexão termina e libera a vaga.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await stream.MoveNextAsync());
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task Stream_frees_the_slot_when_the_client_disconnects()
    {
        var hub = Hub();
        Assert.True(hub.TrySubscribe(out var subscription));
        using var disconnect = new CancellationTokenSource();

        var stream = LiveEndpoints.StreamAsync(subscription!, new LiveOptions(), TimeProvider.System, disconnect.Token)
            .GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); // ready

        var waiting = stream.MoveNextAsync().AsTask();
        await disconnect.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await stream.DisposeAsync();

        Assert.Equal(0, hub.SubscriberCount);
    }
}
