using System.Net;
using System.Net.Sockets;
using DeployGuard.Api.Monitoring;
using DeployGuard.Tests.TestDoubles;

namespace DeployGuard.Tests.Unit;

public class SafeSocketConnectorTests
{
    private static SafeSocketConnector Connector(string resolvedIp, Action<MonitoringOptions>? configure = null) =>
        new(TestOptions.With(configure))
        {
            ResolveAsync = (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolvedIp) }),
        };

    [Fact]
    public async Task Blocks_hostname_that_resolves_to_loopback_dns_rebinding()
    {
        // Cenário: "example.com" passou na allowlist no cadastro, mas o DNS agora aponta para 127.0.0.1.
        var connector = Connector("127.0.0.1");

        await Assert.ThrowsAsync<BlockedDestinationException>(
            () => connector.ResolveAllowedAddressesAsync("example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Blocks_when_any_resolved_address_is_private()
    {
        var connector = new SafeSocketConnector(TestOptions.With())
        {
            ResolveAsync = (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1") }),
        };

        await Assert.ThrowsAsync<BlockedDestinationException>(
            () => connector.ResolveAllowedAddressesAsync("example.com", CancellationToken.None));
    }

    [Fact]
    public async Task Returns_public_addresses()
    {
        var addresses = await Connector("93.184.216.34").ResolveAllowedAddressesAsync("example.com", CancellationToken.None);

        Assert.Equal([IPAddress.Parse("93.184.216.34")], addresses);
    }

    [Fact]
    public async Task Allows_private_address_for_explicitly_authorized_host()
    {
        var connector = Connector("172.20.0.5", o => o.AllowedPrivateHosts = ["demo-app"]);

        var addresses = await connector.ResolveAllowedAddressesAsync("demo-app", CancellationToken.None);

        Assert.Single(addresses);
    }

    [Fact]
    public async Task Real_http_request_through_handler_is_blocked_before_any_byte_is_sent()
    {
        // Servidor TCP real em loopback: se a proteção falhar, ele receberia uma conexão.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var connector = Connector("127.0.0.1");
        using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = connector.ConnectAsync, UseProxy = false });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://example.com:{port}/"));

        Assert.IsType<BlockedDestinationException>(ex.InnerException);
        Assert.False(listener.Pending());
    }
}
