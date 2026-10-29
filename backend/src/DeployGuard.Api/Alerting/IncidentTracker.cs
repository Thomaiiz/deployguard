using DeployGuard.Api.Domain;
using DeployGuard.Api.Monitoring;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Alerting;

public enum IncidentTransition
{
    None,
    Open,
    Resolve,
}

/// <summary>
/// Máquina de estados do incidente. É pura (não acessa banco nem rede),
/// então dá para testar todas as transições com testes de unidade simples.
///
///   Saudável --falha x limiar--> Incidente aberto --primeira resposta OK--> Saudável
/// </summary>
public sealed class IncidentTracker(IOptionsMonitor<AlertingOptions> options)
{
    public (int ConsecutiveFailures, IncidentTransition Transition) Evaluate(
        int consecutiveFailures, bool hasOpenIncident, HealthCheckOutcome outcome)
    {
        // Degraded respondeu (só que devagar): não é queda.
        if (outcome.Status != CheckStatus.Down)
        {
            return (0, hasOpenIncident ? IncidentTransition.Resolve : IncidentTransition.None);
        }

        var failures = consecutiveFailures + 1;
        var shouldOpen = !hasOpenIncident && failures >= options.CurrentValue.FailureThreshold;
        return (failures, shouldOpen ? IncidentTransition.Open : IncidentTransition.None);
    }
}
