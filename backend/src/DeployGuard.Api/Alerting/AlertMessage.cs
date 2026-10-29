using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;

namespace DeployGuard.Api.Alerting;

/// <summary>Dados de um evento de incidente, independentes do formato do canal.</summary>
public sealed record AlertMessage(
    AlertEventType EventType,
    long IncidentId,
    Guid ApplicationId,
    string ApplicationName,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt,
    CheckErrorType Cause,
    string? Summary,
    string? DashboardUrl,
    SuspectedDeployment? SuspectedDeployment = null)
{
    public string Text()
    {
        if (EventType == AlertEventType.IncidentOpened)
        {
            return $"🔴 {ApplicationName} está FORA DO AR desde {StartedAt:dd/MM HH:mm:ss} UTC. Causa: {(Summary ?? Cause.ToString()).TrimEnd('.')}."
                + (SuspectedDeployment is { } d ? $" Último deploy: {d.Version}, {DeployAgo(d.MinutesBeforeIncident)}." : "")
                + (DashboardUrl is null ? "" : $" {DashboardUrl}");
        }

        var duration = (ResolvedAt ?? StartedAt) - StartedAt;
        return $"✅ {ApplicationName} voltou ao ar. Ficou indisponível por {FormatDuration(duration)}."
            + (DashboardUrl is null ? "" : $" {DashboardUrl}");
    }

    private static string DeployAgo(int minutes) => minutes < 1 ? "menos de 1 min antes" : $"{minutes} min antes";

    internal static string FormatDuration(TimeSpan duration) => duration.TotalMinutes < 1
        ? $"{(int)duration.TotalSeconds}s"
        : duration.TotalHours < 1
            ? $"{(int)duration.TotalMinutes}min {duration.Seconds}s"
            : $"{(int)duration.TotalHours}h {duration.Minutes}min";
}
