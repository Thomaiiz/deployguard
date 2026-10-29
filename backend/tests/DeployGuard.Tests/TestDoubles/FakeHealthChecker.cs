using DeployGuard.Api.Domain;
using DeployGuard.Api.Monitoring;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>Substitui as chamadas HTTP reais nos testes de integração.</summary>
public sealed class FakeHealthChecker : IHealthChecker
{
    public HealthCheckOutcome NextOutcome { get; set; } = new(CheckStatus.Up, 200, 42, CheckErrorType.None, null);

    /// <summary>Simula um endpoint lento, para as verificações de instâncias diferentes se sobreporem.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<HealthCheckOutcome> CheckAsync(string url, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, ct);
        }

        return NextOutcome;
    }
}
