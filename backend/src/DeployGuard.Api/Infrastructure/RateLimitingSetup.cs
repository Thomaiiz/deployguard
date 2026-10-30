using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace DeployGuard.Api.Infrastructure;

/// <summary>
/// Limites de requisição por IP do cliente (janela fixa). Protege a API contra abuso
/// e, indiretamente, protege os alvos monitorados: cada cadastro gera tráfego de saída.
/// Com RateLimiting:Redis configurado, o contador é um só para todas as réplicas;
/// sem ele, cada réplica conta na própria memória.
/// </summary>
public static class RateLimitingSetup
{
    public const string ReadPolicy = "api-read";
    public const string WritePolicy = "api-write";
    public const string LoginPolicy = "auth-login";
    public const string PublicPolicy = "public-status";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var readPerMinute = configuration.GetValue("RateLimiting:ReadRequestsPerMinute", 120);
        var writePerMinute = configuration.GetValue("RateLimiting:WriteRequestsPerMinute", 20);
        // Login bem mais restrito: freia ataques de força bruta e de "credential stuffing".
        var loginPerMinute = configuration.GetValue("RateLimiting:LoginRequestsPerMinute", 5);
        // Página de status: aberta para qualquer um, então tem cota própria (menor que a de leitura).
        var publicPerMinute = configuration.GetValue("RateLimiting:PublicRequestsPerMinute", 30);

        var redisConnection = configuration["RateLimiting:Redis"];
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            var redisOptions = ConfigurationOptions.Parse(redisConnection);
            // Não derruba a subida da API se o Redis ainda não estiver de pé: reconecta sozinho.
            redisOptions.AbortOnConnectFail = false;
            // Timeouts curtos: um Redis lento não pode segurar todas as requisições da API.
            redisOptions.ConnectTimeout = 2000;
            redisOptions.AsyncTimeout = 500;
            redisOptions.SyncTimeout = 500;
            // Desconectado, falha na hora (e cai no limite local) em vez de enfileirar e esperar.
            redisOptions.BacklogPolicy = BacklogPolicy.FailFast;
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
            services.AddSingleton<RedisRateLimitStore>();
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(ReadPolicy, ctx => FixedWindowByClientIp(ctx, ReadPolicy, readPerMinute));
            limiter.AddPolicy(WritePolicy, ctx => FixedWindowByClientIp(ctx, WritePolicy, writePerMinute));
            limiter.AddPolicy(LoginPolicy, ctx => FixedWindowByClientIp(ctx, LoginPolicy, loginPerMinute));
            limiter.AddPolicy(PublicPolicy, ctx => FixedWindowByClientIp(ctx, PublicPolicy, publicPerMinute));

            limiter.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowByClientIp(HttpContext context, string policy, int permitsPerMinute)
    {
        // A "partição" é a chave do contador: cada IP tem a sua cota.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var options = new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        };

        var redis = context.RequestServices.GetService<RedisRateLimitStore>();
        if (redis is null)
        {
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => options);
        }

        // O limitador local fica de reserva, para quando o Redis não responder.
        return RateLimitPartition.Get(ip, key => new RedisFixedWindowRateLimiter(
            redis, $"{policy}:{key}", permitsPerMinute, options.Window, new FixedWindowRateLimiter(options)));
    }
}
