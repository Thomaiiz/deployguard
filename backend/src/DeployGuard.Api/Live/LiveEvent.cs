using DeployGuard.Api.Domain;

namespace DeployGuard.Api.Live;

public enum LiveEventType
{
    CheckCompleted,
    IncidentOpened,
    IncidentResolved,
    DeploymentRegistered,
}

/// <summary>
/// Aviso curto de que algo mudou. O dashboard usa o aviso para buscar os dados atualizados
/// na API, em vez de montar o estado a partir dos eventos: assim não existe "estado duplicado"
/// que possa divergir, e perder um evento não deixa a tela errada.
/// </summary>
public sealed record LiveEvent(
    LiveEventType Type,
    Guid ApplicationId,
    DateTimeOffset At,
    CheckStatus? Status = null,
    int? LatencyMs = null,
    string? Version = null)
{
    /// <summary>Nome do evento no protocolo SSE (linha "event:").</summary>
    public string EventName => Type switch
    {
        LiveEventType.CheckCompleted => "check",
        LiveEventType.IncidentOpened => "incident-opened",
        LiveEventType.IncidentResolved => "incident-resolved",
        _ => "deployment",
    };
}
