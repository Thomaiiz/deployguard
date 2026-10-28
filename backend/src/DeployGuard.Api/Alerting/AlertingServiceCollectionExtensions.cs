namespace DeployGuard.Api.Alerting;

public static class AlertingServiceCollectionExtensions
{
    public static IServiceCollection AddAlerting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AlertingOptions>()
            .Bind(configuration.GetSection(AlertingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(o => o.ActiveChannels.All(c => !string.IsNullOrWhiteSpace(c.Name)),
                "Todo webhook precisa de um Name.")
            .Validate(o => o.ActiveChannels.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == o.ActiveChannels.Count,
                "Os nomes dos webhooks devem ser únicos.")
            .Validate(o => o.ActiveChannels.All(c => IsAllowedUrl(c.Url, o.AllowInsecureWebhooks)),
                "Webhooks devem usar URLs https absolutas.")
            .ValidateOnStart();

        services.AddSingleton<IncidentTracker>();
        services.AddSingleton<IAlertSender, WebhookAlertSender>();

        services.AddHttpClient(WebhookAlertSender.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

        services.AddSingleton<AlertDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertDispatcher>());

        return services;
    }

    private static bool IsAllowedUrl(string url, bool allowInsecure) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (allowInsecure && uri.Scheme == Uri.UriSchemeHttp));
}
