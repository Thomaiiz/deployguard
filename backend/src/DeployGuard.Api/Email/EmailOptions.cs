using System.ComponentModel.DataAnnotations;
using MailKit.Security;

namespace DeployGuard.Api.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Servidor SMTP. Vazio = envio desligado (os e-mails são descartados, com aviso no log).</summary>
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// StartTls (porta 587) ou SslOnConnect (porta 465) em produção. None só para servidores
    /// locais de teste, como o Mailpit do Docker Compose.
    /// </summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    public string? Username { get; set; }

    /// <summary>SEGREDO: vem só de variável de ambiente / user-secrets.</summary>
    public string? Password { get; set; }

    [Required, EmailAddress]
    public string FromAddress { get; set; } = "deployguard@localhost";

    public string FromName { get; set; } = "DeployGuard";

    [Range(1, 60)]
    public int TimeoutSeconds { get; set; } = 10;

    public bool Enabled => !string.IsNullOrWhiteSpace(SmtpHost);

    /// <summary>
    /// Sem TLS (None) ou com TLS "se der" (Auto e StartTlsWhenAvailable, que um atacante no meio
    /// do caminho desliga), a senha do SMTP e o link de "esqueci minha senha" viajam em texto puro.
    /// Isso só é aceito para um servidor local de teste: "localhost", um IP de loopback ou um nome
    /// sem ponto (serviço do Docker Compose, como o "mailpit"), e sem usuário/senha.
    /// Um provedor de verdade (smtp.provedor.com) exige StartTls ou SslOnConnect.
    /// </summary>
    public static bool IsTransportSafe(EmailOptions o)
    {
        if (!o.Enabled || o.Security is SecureSocketOptions.StartTls or SecureSocketOptions.SslOnConnect)
        {
            return true;
        }

        var host = o.SmtpHost!.Trim();
        var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip))
            || (!host.Contains('.') && !host.Contains(':'));

        return isLocal && string.IsNullOrEmpty(o.Username);
    }
}
