using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Live;

public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary>Conexões ao vivo simultâneas por instância. Cada uma ocupa memória e um socket.</summary>
    [Range(1, 10_000)]
    public int MaxConnections { get; set; } = 100;

    /// <summary>Envia um "ping" quando não há eventos, para proxies não fecharem a conexão parada.</summary>
    [Range(5, 300)]
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// Fecha a conexão depois desse tempo. O navegador reconecta sozinho, e cada reconexão
    /// passa pela autenticação de novo: quem saiu ou foi removido para de receber eventos.
    /// </summary>
    [Range(1, 60)]
    public int MaxStreamMinutes { get; set; } = 5;

    /// <summary>Eventos guardados por conexão lenta antes de descartar os mais antigos.</summary>
    [Range(10, 10_000)]
    public int BufferPerConnection { get; set; } = 100;
}
