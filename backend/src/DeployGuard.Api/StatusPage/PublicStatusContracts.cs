namespace DeployGuard.Api.StatusPage;

// Contrato da página pública. É de propósito bem menor que o ApplicationResponse:
// nada de Id, URL, latência ou mensagem de erro. Tudo o que está aqui pode ser visto
// por qualquer pessoa na internet.

public enum ComponentStatus
{
    /// <summary>Respondendo normalmente.</summary>
    Operational,

    /// <summary>Lenta, ou com uma falha isolada que ainda não virou incidente.</summary>
    Degraded,

    /// <summary>Incidente aberto: falhou várias vezes seguidas.</summary>
    Outage,

    /// <summary>Ainda não foi verificada.</summary>
    Unknown,
}

public sealed record DailyAvailability(DateOnly Date, double? AvailabilityPercent);

public sealed record PublicComponent(
    string Name,
    ComponentStatus Status,
    double? AvailabilityPercent,
    DateTimeOffset? IncidentSince,
    IReadOnlyList<DailyAvailability> Days);

public sealed record PublicStatusResponse(
    string Title,
    ComponentStatus Overall,
    DateTimeOffset GeneratedAt,
    int HistoryDays,
    IReadOnlyList<PublicComponent> Components);
