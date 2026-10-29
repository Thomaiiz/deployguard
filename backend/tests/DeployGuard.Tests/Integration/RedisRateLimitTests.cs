using System.Net;
using System.Net.Http.Json;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Infrastructure;
using DeployGuard.Tests.TestDoubles;
using Microsoft.AspNetCore.Mvc.Testing;
using StackExchange.Redis;

namespace DeployGuard.Tests.Integration;

/// <summary>Duas instâncias da API no mesmo Redis, como as réplicas do Docker Compose.</summary>
public sealed class RedisRateLimitTests : IClassFixture<RedisRateLimitApiFactory>, IDisposable
{
    private readonly RedisRateLimitApiFactory _factory;
    private readonly WebApplicationFactory<Program> _b;

    public RedisRateLimitTests(RedisRateLimitApiFactory factory)
    {
        _factory = factory;
        _factory.CreateClient().Dispose();
        _b = factory.WithWebHostBuilder(_ => { });
        _b.CreateClient().Dispose();
        ClearCounters();
    }

    public void Dispose() => _b.Dispose();

    private static void ClearCounters()
    {
        using var redis = ConnectionMultiplexer.Connect(RedisTestServer.ConnectionString);
        var db = redis.GetDatabase();
        foreach (var server in redis.GetServers())
        {
            foreach (var key in server.Keys(pattern: RedisRateLimitStore.KeyPrefix + "*"))
            {
                db.KeyDelete(key);
            }
        }
    }

    private static Task<HttpResponseMessage> WrongLoginAsync(WebApplicationFactory<Program> host) =>
        host.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("ninguem@example.com", "senha-errada-123456"));

    [Fact]
    public async Task Both_instances_share_one_limit()
    {
        // Limite de 3 por minuto. Sem o Redis, cada instância aceitaria 3 (6 no total).
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(_factory)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(_b)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WrongLoginAsync(_factory)).StatusCode);

        var fourth = await WrongLoginAsync(_b);

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        var retryAfter = int.Parse(fourth.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public async Task Each_policy_has_its_own_counter()
    {
        for (var i = 0; i < 3; i++)
        {
            await WrongLoginAsync(_factory);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await WrongLoginAsync(_b)).StatusCode);
        // As leituras públicas não gastam a cota do login.
        Assert.Equal(HttpStatusCode.OK, (await _b.CreateClient().GetAsync("/api/public/status")).StatusCode);
    }

    [Fact]
    public async Task The_counter_expires_with_the_window()
    {
        await WrongLoginAsync(_factory);

        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisTestServer.ConnectionString);
        var ttl = await redis.GetDatabase().KeyTimeToLiveAsync($"{RedisRateLimitStore.KeyPrefix}{RateLimitingSetup.LoginPolicy}:unknown");

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
    }
}

public sealed class UnreachableRedisTests(UnreachableRedisApiFactory factory) : IClassFixture<UnreachableRedisApiFactory>
{
    [Fact]
    public async Task Without_Redis_the_api_keeps_working_with_the_local_limit()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ninguem@example.com", "senha-errada-123456"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // O limite continua valendo, agora contado só nesta instância.
        var blocked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("ninguem@example.com", "senha-errada-123456"));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
}
