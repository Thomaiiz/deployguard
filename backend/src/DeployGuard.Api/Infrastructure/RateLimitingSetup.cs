using System.Threading.RateLimiting;

namespace DeployGuard.Api.Infrastructure;

/// <summary>
/// Limites de requisição por IP do cliente (janela fixa). Protege a API contra abuso
/// e, indiretamente, protege os alvos monitorados: cada cadastro gera tráfego de saída.
/// </summary>
public static class RateLimitingSetup
{
    public const string ReadPolicy = "api-read";
    public const string WritePolicy = "api-write";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var readPerMinute = configuration.GetValue("RateLimiting:ReadRequestsPerMinute", 120);
        var writePerMinute = configuration.GetValue("RateLimiting:WriteRequestsPerMinute", 20);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(ReadPolicy, ctx => FixedWindowByClientIp(ctx, readPerMinute));
            limiter.AddPolicy(WritePolicy, ctx => FixedWindowByClientIp(ctx, writePerMinute));

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

    private static RateLimitPartition<string> FixedWindowByClientIp(HttpContext context, int permitsPerMinute) =>
        // A "partição" é a chave do contador: cada IP tem a sua cota.
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
}
