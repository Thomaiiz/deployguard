using DeployGuard.Api.Monitoring;
using Microsoft.Extensions.Options;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>IOptionsMonitor fixo, para testar classes sem subir o host inteiro.</summary>
public sealed class TestOptions(MonitoringOptions value) : IOptionsMonitor<MonitoringOptions>
{
    public MonitoringOptions CurrentValue { get; } = value;

    public MonitoringOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<MonitoringOptions, string?> listener) => null;

    public static TestOptions With(Action<MonitoringOptions>? configure = null)
    {
        var options = new MonitoringOptions { AllowedHosts = ["example.com", "*.example.org"] };
        configure?.Invoke(options);
        return new TestOptions(options);
    }
}
