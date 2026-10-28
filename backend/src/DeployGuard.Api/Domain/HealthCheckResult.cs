namespace DeployGuard.Api.Domain;

/// <summary>Uma verificação executada: registro histórico imutável.</summary>
public sealed class HealthCheckResult
{
    public long Id { get; set; }

    public Guid ApplicationId { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    public CheckStatus Status { get; set; }

    public int? HttpStatusCode { get; set; }

    public int LatencyMs { get; set; }

    public CheckErrorType ErrorType { get; set; }

    public string? ErrorMessage { get; set; }
}
