using DeployGuard.Api.Domain;

namespace DeployGuard.Api.History;

public sealed record HealthCheckResultResponse(
    long Id,
    DateTimeOffset CheckedAt,
    CheckStatus Status,
    int? HttpStatusCode,
    int LatencyMs,
    CheckErrorType ErrorType,
    string? ErrorMessage);

/// <summary>Página do histórico. <c>NextCursor</c> nulo significa que não há mais itens.</summary>
public sealed record HealthCheckPage(IReadOnlyList<HealthCheckResultResponse> Items, string? NextCursor);

public sealed record ApplicationStatsResponse(
    Guid ApplicationId,
    DateTimeOffset From,
    DateTimeOffset To,
    int TotalChecks,
    double? AvailabilityPercent,
    double? AverageLatencyMs,
    int? P95LatencyMs,
    int? MaxLatencyMs,
    IReadOnlyDictionary<CheckStatus, int> ByStatus,
    IReadOnlyDictionary<CheckErrorType, int> Errors);
