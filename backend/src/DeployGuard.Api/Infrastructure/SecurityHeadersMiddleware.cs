namespace DeployGuard.Api.Infrastructure;

public static class SecurityHeadersMiddleware
{
    /// <summary>Cabeçalhos defensivos básicos para respostas JSON da API.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.CacheControl = "no-store";
            await next();
        });
}
