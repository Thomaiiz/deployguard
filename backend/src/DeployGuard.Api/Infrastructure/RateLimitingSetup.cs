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

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(ReadPolicy, ctx => FixedWindowByClientIp(ctx, readPerMinute));
            limiter.AddPolicy(WritePolicy, ctx => FixedWindowByClientIp(ctx, writePerMinute));
            limiter.AddPolicy(LoginPolicy, ctx => FixedWindowByClientIp(ctx, loginPerMinute));
            limiter.AddPolicy(PublicPolicy, ctx => FixedWindowByClientIp(ctx, publicPerMinute));

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
