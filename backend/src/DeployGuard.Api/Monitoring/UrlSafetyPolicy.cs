using System.Net;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Monitoring;

public sealed record UrlValidationResult(Uri? Uri, string? Error)
{
    public bool IsValid => Error is null;

    public static UrlValidationResult Ok(Uri uri) => new(uri, null);

    public static UrlValidationResult Fail(string error) => new(null, error);
}

/// <summary>
/// Validação estática de uma URL de health check (sem acessar a rede).
/// Roda no cadastro e de novo antes de cada verificação, porque a configuração
/// pode ter mudado. A checagem do IP real acontece depois, na conexão
/// (veja <see cref="SafeSocketConnector"/>).
/// </summary>
public sealed class UrlSafetyPolicy(IOptionsMonitor<MonitoringOptions> options)
{
    public const int MaxUrlLength = 2048;

    public UrlValidationResult Validate(string? rawUrl)
    {
        var settings = options.CurrentValue;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return UrlValidationResult.Fail("A URL é obrigatória.");
        }

        if (rawUrl.Length > MaxUrlLength)
        {
            return UrlValidationResult.Fail($"A URL deve ter no máximo {MaxUrlLength} caracteres.");
        }

        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return UrlValidationResult.Fail("A URL não é válida.");
        }

        var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || (settings.AllowHttp && uri.Scheme == Uri.UriSchemeHttp);
        if (!schemeAllowed)
        {
            return UrlValidationResult.Fail(settings.AllowHttp
                ? "Apenas http e https são permitidos."
                : "Apenas https é permitido.");
        }

        // Credenciais na URL (https://user:senha@host) acabariam salvas no banco e em logs.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return UrlValidationResult.Fail("A URL não pode conter usuário ou senha.");
        }

        if (!settings.EffectivePorts.Contains(uri.Port))
        {
            return UrlValidationResult.Fail($"A porta {uri.Port} não é permitida.");
        }

        // IdnHost normaliza domínios internacionais (punycode) para comparar com a allowlist.
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

        if (!IsHostAllowed(host, settings))
        {
            return UrlValidationResult.Fail($"O host '{host}' não está na lista de hosts autorizados.");
        }

        // IP literal já pode ser avaliado aqui; nomes só podem ser avaliados após o DNS.
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var ip)
            && !IpAddressGuard.IsPublic(ip)
            && !settings.IsPrivateHostAllowed(host))
        {
            return UrlValidationResult.Fail("Destinos em redes privadas ou locais não são permitidos.");
        }

        return UrlValidationResult.Ok(uri);
    }

    private static bool IsHostAllowed(string host, MonitoringOptions settings)
    {
        if (settings.IsPrivateHostAllowed(host))
        {
            return true;
        }

        foreach (var pattern in settings.AllowedHosts)
        {
            var normalized = pattern.Trim().TrimEnd('.').ToLowerInvariant();

            if (normalized == "*")
            {
                return true;
            }

            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                // "*.exemplo.com" casa "api.exemplo.com", mas não "exemplo.com" nem "malexemplo.com".
                if (host.EndsWith(normalized[1..], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (host == normalized)
            {
                return true;
            }
        }

        return false;
    }
}
