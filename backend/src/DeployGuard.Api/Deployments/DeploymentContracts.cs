using DeployGuard.Api.Domain;

namespace DeployGuard.Api.Deployments;

public sealed record CreateDeploymentRequest(
    string? Version,
    string? CommitSha,
    string? Description,
    string? Url,
    DateTimeOffset? DeployedAt);

public sealed record DeploymentResponse(
    long Id,
    Guid ApplicationId,
    string ApplicationName,
    string Version,
    string? CommitSha,
    string? Description,
    string? Url,
    DateTimeOffset DeployedAt,
    string RegisteredBy);

/// <summary>Deploy que aconteceu pouco antes do início de um incidente.</summary>
public sealed record SuspectedDeployment(long Id, string Version, string? CommitSha, DateTimeOffset DeployedAt, int MinutesBeforeIncident)
{
    public static SuspectedDeployment? From(Deployment? d, DateTimeOffset incidentStartedAt) => d is null
        ? null
        : new(d.Id, d.Version, d.CommitSha, d.DeployedAt, (int)(incidentStartedAt - d.DeployedAt).TotalMinutes);
}

public sealed record CreateApiKeyRequest(string? Name);

public sealed record ApiKeyResponse(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, string CreatedBy, DateTimeOffset? LastUsedAt)
{
    public static ApiKeyResponse From(ApiKey k) => new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt);
}

/// <summary>Única resposta que contém a chave completa. Depois disso, nem o Admin consegue vê-la de novo.</summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse ApiKey, string Key);
