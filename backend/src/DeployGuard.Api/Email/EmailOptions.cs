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
}
