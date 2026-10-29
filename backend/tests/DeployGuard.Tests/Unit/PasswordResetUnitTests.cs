using DeployGuard.Api.Auth;

namespace DeployGuard.Tests.Unit;

public class PasswordResetUnitTests
{
    [Fact]
    public void Tokens_are_long_random_and_url_safe()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => PasswordResetEndpoints.NewToken()).ToList();

        Assert.Equal(100, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length); // 32 bytes em Base64 de URL, sem "="
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });
    }

    [Fact]
    public void The_email_puts_the_token_in_the_fragment_and_states_the_expiry()
    {
        var settings = new PasswordResetOptions { DashboardBaseUrl = "https://dg.example.com/", TokenLifetimeMinutes = 30 };

        var message = PasswordResetEndpoints.BuildEmail("ana@example.com", "abc123", settings);

        Assert.Equal("ana@example.com", message.To);
        Assert.Contains("https://dg.example.com/reset-password#token=abc123", message.Body);
        Assert.Contains("30 minutos", message.Body);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://dg.example.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("dg.example.com", false)]
    [InlineData("ftp://dg.example.com", false)]
    [InlineData("https://dg.example.com/?next=x", false)]
    public void Only_absolute_http_urls_are_accepted_as_dashboard_base(string? value, bool valid)
    {
        Assert.Equal(valid, PasswordResetOptions.IsValidBaseUrl(value));
    }
}
