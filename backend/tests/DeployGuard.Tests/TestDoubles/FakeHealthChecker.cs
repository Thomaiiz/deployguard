using DeployGuard.Api.Domain;
using DeployGuard.Api.Monitoring;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>Substitui as chamadas HTTP reais nos testes de integração.</summary>
public sealed class FakeHealthChecker : IHealthChecker
{
    public HealthCheckOutcome NextOutcome { get; set; } = new(CheckStatus.Up, 200, 42, CheckErrorType.None, null);

    public Task<HealthCheckOutcome> CheckAsync(string url, CancellationToken ct) => Task.FromResult(NextOutcome);
}
