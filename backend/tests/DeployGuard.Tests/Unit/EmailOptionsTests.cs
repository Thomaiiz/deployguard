using DeployGuard.Api.Email;
using MailKit.Security;

namespace DeployGuard.Tests.Unit;

public class EmailOptionsTests
{
    private static EmailOptions Options(string? host, SecureSocketOptions security, string? username = null) =>
        new() { SmtpHost = host, Security = security, Username = username };

    [Theory]
    [InlineData("smtp.provedor.com", SecureSocketOptions.None)]
    [InlineData("smtp.provedor.com", SecureSocketOptions.Auto)]
    [InlineData("smtp.provedor.com", SecureSocketOptions.StartTlsWhenAvailable)]
    [InlineData("203.0.113.5", SecureSocketOptions.None)]
    public void A_real_smtp_server_without_mandatory_tls_is_refused(string host, SecureSocketOptions security)
    {
        Assert.False(EmailOptions.IsTransportSafe(Options(host, security)));
    }

    [Fact]
    public void Credentials_never_travel_without_tls_even_to_a_local_server()
    {
        Assert.False(EmailOptions.IsTransportSafe(Options("mailpit", SecureSocketOptions.None, username: "usuario")));
    }

    [Theory]
    [InlineData("smtp.provedor.com", SecureSocketOptions.StartTls)]
    [InlineData("smtp.provedor.com", SecureSocketOptions.SslOnConnect)]
    [InlineData("mailpit", SecureSocketOptions.None)]
    [InlineData("localhost", SecureSocketOptions.None)]
    [InlineData("127.0.0.1", SecureSocketOptions.None)]
    [InlineData(null, SecureSocketOptions.None)] // envio desligado
    public void Tls_or_a_local_test_server_is_accepted(string? host, SecureSocketOptions security)
    {
        Assert.True(EmailOptions.IsTransportSafe(Options(host, security)));
    }
}
