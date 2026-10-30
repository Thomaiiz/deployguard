using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace DeployGuard.Api.Infrastructure;

/// <summary>
/// Contador de requisições compartilhado entre todas as réplicas da API, guardado no Redis.
/// Sem ele, cada réplica conta sozinha: com 2 réplicas atrás do nginx, o limite real dobra.
/// </summary>
public sealed class RedisRateLimitStore(IConnectionMultiplexer redis, TimeProvider time, ILogger<RedisRateLimitStore> logger)
{
    public const string KeyPrefix = "deployguard:ratelimit:";

    // Script Lua: o Redis executa tudo de uma vez, sem nada no meio (atômico).
    // INCR soma 1; na primeira requisição da janela, define quando a chave expira.
    // O "ttl < 0" cobre uma chave que, por algum motivo, ficou sem prazo.
    private static readonly LuaScript IncrementScript = LuaScript.Prepare("""
        local count = redis.call('INCR', @key)
        if count == 1 then redis.call('PEXPIRE', @key, @windowMs) end
        local ttl = redis.call('PTTL', @key)
        if ttl < 0 then
          redis.call('PEXPIRE', @key, @windowMs)
          ttl = tonumber(@windowMs)
        end
        return { count, ttl }
        """);

    private long _lastWarningTicks;

    /// <summary>
    /// Soma 1 no contador da chave e devolve o total na janela e quanto falta para ela acabar.
    /// Devolve null se o Redis não responder: quem chama decide o que fazer (aqui, usar o
    /// contador local da réplica).
    /// </summary>
    public async Task<(long Count, TimeSpan ResetIn)?> IncrementAsync(string key, TimeSpan window)
    {
        try
        {
            var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
                IncrementScript, new { key = (RedisKey)(KeyPrefix + key), windowMs = (long)window.TotalMilliseconds }))!;
            return ((long)result[0], TimeSpan.FromMilliseconds((long)result[1]));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            WarnAtMostOncePerMinute(ex);
            return null;
        }
    }

    private void WarnAtMostOncePerMinute(Exception ex)
    {
        // Com o Redis fora do ar, cada requisição falharia aqui: sem esse freio, o log viraria spam.
        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastWarningTicks);
        if (now - last >= TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastWarningTicks, now, last) == last)
        {
            logger.LogWarning("Redis indisponível para o rate limit ({Error}). Usando o limite local de cada réplica.", ex.Message);
        }
    }
}

/// <summary>
/// Janela fixa no Redis para uma partição (um IP numa política). Se o Redis cair, a API continua
/// atendendo com o limitador em memória da própria réplica (fail-open para o local, não para "sem
/// limite"): perder o Redis não derruba o login nem libera abuso sem freio.
/// </summary>
public sealed class RedisFixedWindowRateLimiter(
    RedisRateLimitStore store, string key, int permitLimit, TimeSpan window, RateLimiter fallback) : RateLimiter
{
    public override TimeSpan? IdleDuration => fallback.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => fallback.GetStatistics();

    /// <summary>
    /// A versão síncrona não pode esperar o Redis. Devolver "não concedido" sem metadados faz o
    /// middleware do ASP.NET Core chamar a versão assíncrona logo em seguida.
    /// </summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Lease.Pending;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var result = await store.IncrementAsync(key, window);
        if (result is null)
        {
            return await fallback.AcquireAsync(permitCount, cancellationToken);
        }

        var (count, resetIn) = result.Value;
        return count <= permitLimit ? Lease.Granted : Lease.Rejected(resetIn);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            fallback.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Lease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Granted = new(true, null);
        public static readonly Lease Pending = new(false, null);

        public static Lease Rejected(TimeSpan retryAfter) => new(false, retryAfter);

        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames =>
            retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is not null && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
