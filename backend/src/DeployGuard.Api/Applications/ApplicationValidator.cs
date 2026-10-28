using DeployGuard.Api.Monitoring;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Applications;

/// <summary>
/// Validação de entrada do cadastro. Retorna erros no formato que
/// <c>Results.ValidationProblem</c> espera (campo → mensagens).
/// </summary>
public sealed class ApplicationValidator(UrlSafetyPolicy urlPolicy, IOptionsMonitor<MonitoringOptions> options)
{
    public const int MaxNameLength = 100;

    public Dictionary<string, string[]> Validate(string? name, string? url, int intervalSeconds)
    {
        var errors = new Dictionary<string, string[]>();
        var settings = options.CurrentValue;

        var trimmedName = name?.Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            errors["name"] = ["O nome é obrigatório."];
        }
        else if (trimmedName.Length > MaxNameLength)
        {
            errors["name"] = [$"O nome deve ter no máximo {MaxNameLength} caracteres."];
        }

        var urlResult = urlPolicy.Validate(url);
        if (!urlResult.IsValid)
        {
            errors["healthCheckUrl"] = [urlResult.Error!];
        }

        if (intervalSeconds < settings.MinIntervalSeconds || intervalSeconds > settings.MaxIntervalSeconds)
        {
            errors["checkIntervalSeconds"] =
                [$"O intervalo deve estar entre {settings.MinIntervalSeconds} e {settings.MaxIntervalSeconds} segundos."];
        }

        return errors;
    }
}
