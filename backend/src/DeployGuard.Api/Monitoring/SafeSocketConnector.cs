using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Monitoring;

/// <summary>Lançada quando o destino resolve para um IP que não é permitido.</summary>
public sealed class BlockedDestinationException(string host)
    : Exception($"O destino '{host}' resolve para um endereço não permitido.");

/// <summary>
/// Abre a conexão TCP do HttpClient de health checks (via <c>SocketsHttpHandler.ConnectCallback</c>).
///
/// Por que validar aqui e não só no cadastro? Por causa de DNS rebinding: um atacante
/// controla o DNS de "evil.com", que resolve para um IP público no cadastro e para
/// 127.0.0.1 minutos depois. Validando o IP no momento exato da conexão, e conectando
/// justamente nos IPs que validamos, não existe janela entre "checar" e "usar"
/// (o problema clássico TOCTOU, time-of-check to time-of-use).
/// </summary>
public sealed class SafeSocketConnector(IOptionsMonitor<MonitoringOptions> options)
{
    /// <summary>Resolução DNS substituível nos testes.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> ResolveAsync { get; init; } =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await ResolveAllowedAddressesAsync(endpoint.Host, ct);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<IPAddress[]> ResolveAllowedAddressesAsync(string host, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await ResolveAsync(host, ct);

        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        if (options.CurrentValue.IsPrivateHostAllowed(host))
        {
            return addresses;
        }

        // Se QUALQUER endereço for privado, recusamos tudo: um host público
        // "honesto" não tem motivo para também anunciar IPs internos.
        if (addresses.Any(a => !IpAddressGuard.IsPublic(a)))
        {
            throw new BlockedDestinationException(host);
        }

        return addresses;
    }
}
