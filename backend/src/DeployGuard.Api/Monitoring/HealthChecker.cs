using System.Diagnostics;
using System.Net.Sockets;
using DeployGuard.Api.Domain;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Monitoring;

public sealed record HealthCheckOutcome(
    CheckStatus Status,
    int? HttpStatusCode,
    int LatencyMs,
    CheckErrorType ErrorType,
    string? ErrorMessage);

public interface IHealthChecker
{
    Task<HealthCheckOutcome> CheckAsync(string url, CancellationToken ct);
}

/// <summary>Executa uma verificação HTTP e traduz o resultado (ou a falha) para o domínio.</summary>
public sealed class HealthChecker(
    IHttpClientFactory httpClientFactory,
    UrlSafetyPolicy urlPolicy,
    IOptionsMonitor<MonitoringOptions> options,
    TimeProvider time,
    ILogger<HealthChecker> logger) : IHealthChecker
{
    public const string HttpClientName = "health-checks";

    public async Task<HealthCheckOutcome> CheckAsync(string url, CancellationToken ct)
    {
        var settings = options.CurrentValue;

        // Revalida: a allowlist pode ter mudado depois do cadastro.
        var validation = urlPolicy.Validate(url);
        if (!validation.IsValid)
        {
            return Failure(CheckErrorType.BlockedDestination, 0, "URL não autorizada pela política atual.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        // Timeout total da verificação (DNS + conexão + TLS + cabeçalhos).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

        var startedAt = time.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
            request.Headers.UserAgent.ParseAdd("DeployGuard-HealthChecker/1.0");

            // ResponseHeadersRead: só esperamos os cabeçalhos. O corpo nunca é baixado,
            // então um alvo malicioso não consegue nos fazer ler gigabytes.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var latency = ElapsedMs(startedAt);
            var statusCode = (int)response.StatusCode;

            if (statusCode is >= 200 and < 300)
            {
                var status = latency > settings.DegradedLatencyThresholdMs ? CheckStatus.Degraded : CheckStatus.Up;
                return new HealthCheckOutcome(status, statusCode, latency, CheckErrorType.None, null);
            }

            // Redirecionamentos não são seguidos (um 302 poderia apontar para a rede interna).
            return new HealthCheckOutcome(
                CheckStatus.Down, statusCode, latency, CheckErrorType.UnexpectedStatusCode, $"HTTP {statusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failure(CheckErrorType.Timeout, ElapsedMs(startedAt), $"Sem resposta em {settings.RequestTimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            var errorType = Classify(ex);
            logger.LogInformation("Health check falhou para {Host}: {ErrorType}", validation.Uri!.Host, errorType);
            return Failure(errorType, ElapsedMs(startedAt), MessageFor(errorType));
        }
    }

    private static HealthCheckOutcome Failure(CheckErrorType type, int latencyMs, string message) =>
        new(CheckStatus.Down, null, latencyMs, type, message);

    private int ElapsedMs(long startedAt) => (int)time.GetElapsedTime(startedAt).TotalMilliseconds;

    internal static CheckErrorType Classify(HttpRequestException ex)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case BlockedDestinationException:
                    return CheckErrorType.BlockedDestination;
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return CheckErrorType.DnsFailure;
                case TimeoutException:
                    return CheckErrorType.Timeout;
            }
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => CheckErrorType.DnsFailure,
            HttpRequestError.ConnectionError => CheckErrorType.ConnectionFailed,
            HttpRequestError.SecureConnectionError => CheckErrorType.TlsFailure,
            _ => CheckErrorType.Unknown,
        };
    }

    // Mensagens fixas: a mensagem bruta da exceção pode expor detalhes internos.
    private static string MessageFor(CheckErrorType type) => type switch
    {
        CheckErrorType.BlockedDestination => "Destino bloqueado: resolve para uma rede privada ou local.",
        CheckErrorType.DnsFailure => "Não foi possível resolver o nome do host.",
        CheckErrorType.ConnectionFailed => "Não foi possível conectar ao host.",
        CheckErrorType.TlsFailure => "Falha na negociação TLS (certificado inválido ou expirado?).",
        CheckErrorType.Timeout => "Tempo de conexão esgotado.",
        _ => "Erro inesperado na requisição.",
    };
}
