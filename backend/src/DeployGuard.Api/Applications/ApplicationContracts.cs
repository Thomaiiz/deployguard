using DeployGuard.Api.Domain;

namespace DeployGuard.Api.Applications;

// DTOs (Data Transfer Objects): o contrato público da API. Separados das entidades
// para que o banco possa mudar sem quebrar clientes e para não expor campos internos.

public sealed record CreateApplicationRequest(string? Name, string? HealthCheckUrl, int? CheckIntervalSeconds);

public sealed record UpdateApplicationRequest(string? Name, string? HealthCheckUrl, int? CheckIntervalSeconds, bool? IsEnabled);

public sealed record ApplicationResponse(
    Guid Id,
    string Name,
    string HealthCheckUrl,
    int CheckIntervalSeconds,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastCheckedAt,
    CheckStatus? LastStatus,
    int? LastLatencyMs,
    double? Availability24hPercent,
    DateTimeOffset? OpenIncidentSince)
{
    public static ApplicationResponse From(MonitoredApplication app, double? availability24h, DateTimeOffset? openIncidentSince = null) => new(
        app.Id,
        app.Name,
        app.HealthCheckUrl,
        app.CheckIntervalSeconds,
        app.IsEnabled,
        app.CreatedAt,
        app.LastCheckedAt,
        app.LastStatus,
        app.LastLatencyMs,
        availability24h,
        openIncidentSince);
}
