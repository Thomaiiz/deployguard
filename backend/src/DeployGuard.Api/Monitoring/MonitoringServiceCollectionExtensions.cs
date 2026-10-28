using System.Net;

namespace DeployGuard.Api.Monitoring;

public static class MonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.MinIntervalSeconds <= o.DefaultIntervalSeconds && o.DefaultIntervalSeconds <= o.MaxIntervalSeconds,
                "DefaultIntervalSeconds deve estar entre MinIntervalSeconds e MaxIntervalSeconds.")
            .ValidateOnStart(); // falha rápido: configuração inválida impede a aplicação de subir

        services.AddSingleton<UrlSafetyPolicy>();
        services.AddSingleton<SafeSocketConnector>();
        services.AddSingleton<IHealthChecker, HealthChecker>();

        // IHttpClientFactory gerencia o ciclo de vida dos handlers (evita esgotar sockets
        // e respeita mudanças de DNS). Aqui configuramos o handler "blindado".
        services.AddHttpClient(HealthChecker.HttpClientName, client =>
            {
                // O timeout efetivo é controlado por CancellationToken no HealthChecker;
                // este é só uma rede de segurança.
                client.Timeout = TimeSpan.FromSeconds(35);
            })
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MonitoringOptions>>().Value;
                var connector = sp.GetRequiredService<SafeSocketConnector>();

                return new SocketsHttpHandler
                {
                    ConnectCallback = connector.ConnectAsync,
                    ConnectTimeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds),
                    AllowAutoRedirect = false,           // redirect poderia levar a destino interno
                    UseProxy = false,                    // proxy faria o IP validado ser o do proxy
                    UseCookies = false,                  // verificações não carregam estado
                    AutomaticDecompression = DecompressionMethods.None,
                    MaxResponseHeadersLength = 64,       // KB: limita cabeçalhos enormes
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2), // reabre conexões e revalida DNS
                };
            });

        // Registrado como singleton e como hosted service (mesma instância),
        // para que os testes possam pedir o agendador e rodar uma rodada manualmente.
        services.AddSingleton<HealthCheckScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<HealthCheckScheduler>());

        return services;
    }
}
