using System.Net;
using DeployGuard.Api.Monitoring;

namespace DeployGuard.Tests.Unit;

public class IpAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // metadados de nuvem (AWS, Azure, GCP)
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:10.0.0.1")]  // IPv4 privado mapeado em IPv6
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]   // NAT64 apontando para 10.0.0.1
    [InlineData("2002:a00:1::1")]    // 6to4 embutindo 10.0.0.1
    public void Blocks_private_local_and_special_addresses(string ip)
    {
        Assert.False(IpAddressGuard.IsPublic(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]   // logo fora de 172.16.0.0/12
    [InlineData("11.0.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Allows_public_addresses(string ip)
    {
        Assert.True(IpAddressGuard.IsPublic(IPAddress.Parse(ip)));
    }
}
