using DeployGuard.Api.Alerting;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;

namespace DeployGuard.Tests.Unit;

public class DeploymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Accepts_a_complete_deployment() =>
        Assert.Empty(DeploymentValidator.Validate(
            new CreateDeploymentRequest("v1.2.3", "0123abcd", "ok", "https://ci.example.com/run/1", Now.AddMinutes(-1)), Now));

    [Theory]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relativo")]
    public void Rejects_links_that_are_not_https(string url) =>
        Assert.Contains("url", DeploymentValidator.Validate(new CreateDeploymentRequest("v1", null, null, url, null), Now).Keys);

    [Fact]
    public void Rejects_versions_with_line_breaks() =>
        Assert.Contains("version", DeploymentValidator.Validate(new CreateDeploymentRequest("v1\nFORJADO", null, null, null, null), Now).Keys);

    [Fact]
    public void Rejects_dates_too_old() =>
        Assert.Contains("deployedAt", DeploymentValidator.Validate(new CreateDeploymentRequest("v1", null, null, null, Now.AddDays(-91)), Now).Keys);

    [Fact]
    public void Api_keys_are_random_and_stored_only_as_hash()
    {
        var a = ApiKeys.Generate();
        var b = ApiKeys.Generate();

        Assert.NotEqual(a, b);
        Assert.StartsWith("dg_", a);
        Assert.True(a.Length >= 40); // 32 bytes em Base64URL
        Assert.Equal(64, ApiKeys.Hash(a).Length);
        Assert.Equal(ApiKeys.Hash(a), ApiKeys.Hash(a));
        Assert.Equal(7, ApiKeys.DisplayPrefix(a).Length);
    }

    [Fact]
    public void Alert_mentions_the_suspected_deploy()
    {
        var start = Now;
        var message = new AlertMessage(AlertEventType.IncidentOpened, 1, Guid.NewGuid(), "Loja", start, null,
            CheckErrorType.Timeout, null, null, new SuspectedDeployment(1, "v2.0.0", null, start.AddMinutes(-4), 4));

        Assert.Contains("Último deploy: v2.0.0, 4 min antes.", message.Text());
        Assert.DoesNotContain("Último deploy", (message with { SuspectedDeployment = null }).Text());
    }
}
