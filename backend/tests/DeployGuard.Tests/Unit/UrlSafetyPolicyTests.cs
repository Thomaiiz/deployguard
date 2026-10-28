using DeployGuard.Api.Monitoring;
using DeployGuard.Tests.TestDoubles;

namespace DeployGuard.Tests.Unit;

public class UrlSafetyPolicyTests
{
    private static UrlSafetyPolicy Policy(Action<MonitoringOptions>? configure = null) => new(TestOptions.With(configure));

    [Theory]
    [InlineData("https://example.com/health")]
    [InlineData("https://EXAMPLE.com/health")]
    [InlineData("https://api.example.org/status?full=true")]
    [InlineData("https://a.b.example.org/")]
    public void Accepts_authorized_https_urls(string url)
    {
        Assert.True(Policy().Validate(url).IsValid);
    }

    [Theory]
    [InlineData(null, "obrigatória")]
    [InlineData("   ", "obrigatória")]
    [InlineData("not a url", "não é válida")]
    [InlineData("/relative/path", "https")]          // no Linux, o .NET interpreta como file:///relative/path
    [InlineData("http://example.com/health", "https")]
    [InlineData("ftp://example.com/file", "https")]
    [InlineData("file:///etc/passwd", "https")]
    [InlineData("https://user:secret@example.com/", "usuário ou senha")]
    [InlineData("https://example.com:8443/", "porta")]
    [InlineData("https://evil.com/", "hosts autorizados")]
    [InlineData("https://example.org/", "hosts autorizados")]       // "*.example.org" não inclui o domínio raiz
    [InlineData("https://notexample.org/", "hosts autorizados")]
    [InlineData("https://example.com.evil.com/", "hosts autorizados")]
    public void Rejects_unsafe_or_unauthorized_urls(string? url, string expectedError)
    {
        var result = Policy().Validate(url);

        Assert.False(result.IsValid);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public void Rejects_very_long_urls()
    {
        var url = "https://example.com/" + new string('a', UrlSafetyPolicy.MaxUrlLength);
        Assert.False(Policy().Validate(url).IsValid);
    }

    [Theory]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/")]
    [InlineData("https://10.1.2.3/")]
    public void Rejects_private_ip_literals_even_with_wildcard_allowlist(string url)
    {
        var result = Policy(o => o.AllowedHosts = ["*"]).Validate(url);

        Assert.False(result.IsValid);
        Assert.Contains("redes privadas", result.Error);
    }

    [Fact]
    public void Allows_http_and_custom_ports_only_when_configured()
    {
        var policy = Policy(o =>
        {
            o.AllowHttp = true;
            o.AllowedPorts = [80, 8080];
        });

        Assert.True(policy.Validate("http://example.com:8080/health").IsValid);
        Assert.False(policy.Validate("https://example.com/health").IsValid); // 443 saiu da lista
    }

    [Fact]
    public void Explicitly_allowed_private_host_is_accepted()
    {
        var policy = Policy(o =>
        {
            o.AllowHttp = true;
            o.AllowedPorts = [8080];
            o.AllowedPrivateHosts = ["demo-app"];
        });

        Assert.True(policy.Validate("http://demo-app:8080/health").IsValid);
        Assert.False(policy.Validate("http://other-internal:8080/health").IsValid);
    }
}
