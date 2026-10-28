using System.Net;
using System.Net.Sockets;

namespace DeployGuard.Api.Monitoring;

/// <summary>
/// Decide se um IP é um destino público. É a peça central da proteção contra SSRF
/// (Server-Side Request Forgery): sem ela, alguém poderia cadastrar uma URL que aponta
/// para a rede interna, para o próprio servidor ou para o serviço de metadados da nuvem
/// (169.254.169.254) e usar o DeployGuard como "ponte" para dentro da infraestrutura.
/// </summary>
public static class IpAddressGuard
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        // IPv4
        IPNetwork.Parse("0.0.0.0/8"),        // "esta rede"
        IPNetwork.Parse("10.0.0.0/8"),       // privada (RFC 1918)
        IPNetwork.Parse("100.64.0.0/10"),    // CGNAT
        IPNetwork.Parse("127.0.0.0/8"),      // loopback
        IPNetwork.Parse("169.254.0.0/16"),   // link-local, inclui metadados de nuvem
        IPNetwork.Parse("172.16.0.0/12"),    // privada (RFC 1918)
        IPNetwork.Parse("192.0.0.0/24"),     // atribuições IETF
        IPNetwork.Parse("192.0.2.0/24"),     // documentação
        IPNetwork.Parse("192.168.0.0/16"),   // privada (RFC 1918)
        IPNetwork.Parse("198.18.0.0/15"),    // benchmark
        IPNetwork.Parse("198.51.100.0/24"),  // documentação
        IPNetwork.Parse("203.0.113.0/24"),   // documentação
        IPNetwork.Parse("224.0.0.0/4"),      // multicast
        IPNetwork.Parse("240.0.0.0/4"),      // reservada, inclui broadcast

        // IPv6
        IPNetwork.Parse("::/128"),           // não especificado
        IPNetwork.Parse("::1/128"),          // loopback
        IPNetwork.Parse("64:ff9b::/96"),     // NAT64: embute um IPv4 qualquer
        IPNetwork.Parse("100::/64"),         // descarte
        IPNetwork.Parse("2001::/32"),        // Teredo: embute IPv4
        IPNetwork.Parse("2001:db8::/32"),    // documentação
        IPNetwork.Parse("2002::/16"),        // 6to4: embute IPv4
        IPNetwork.Parse("fc00::/7"),         // ULA (equivalente IPv6 de rede privada)
        IPNetwork.Parse("fe80::/10"),        // link-local
        IPNetwork.Parse("ff00::/8"),         // multicast
    ];

    public static bool IsPublic(IPAddress address)
    {
        // "::ffff:10.0.0.1" é um IPv4 disfarçado de IPv6; avaliamos o IPv4 real.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return false;
        }

        foreach (var network in BlockedNetworks)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address))
            {
                return false;
            }
        }

        return true;
    }
}
