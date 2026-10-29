using System.Net;
using System.Net.Http.Json;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;

namespace DeployGuard.Tests.Integration;

public class RateLimitTests(StrictRateLimitApiFactory factory) : IClassFixture<StrictRateLimitApiFactory>
{
    [Fact]
    public async Task Write_requests_above_limit_get_429()
    {
        var client = factory.CreateAdminClient();
        var request = new CreateApplicationRequest("", "https://example.com/health", 30); // inválido de propósito

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/applications", request)).StatusCode);
        }

        // O rate limiter roda antes da validação: até requisições inválidas consomem cota.
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], statuses);
    }
}

public class ApplicationLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Cannot_register_more_than_max_applications()
    {
        var client = factory.CreateAdminClient();
        HttpResponseMessage? last = null;

        for (var i = 0; i < 6; i++) // MaxApplications = 5 no ApiFactory
        {
            last = await client.PostAsJsonAsync("/api/applications",
                new CreateApplicationRequest($"App {i}", "https://example.com/health", 30));
        }

        Assert.Equal(HttpStatusCode.Conflict, last!.StatusCode);
    }
}

public class LoginRateLimitTests(StrictRateLimitApiFactory factory) : IClassFixture<StrictRateLimitApiFactory>
{
    [Fact]
    public async Task Login_attempts_above_limit_get_429()
    {
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) // LoginRequestsPerMinute = 3 no StrictRateLimitApiFactory
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest(ApiFactory.AdminEmail, "senha-errada-de-proposito"))).StatusCode);
        }

        // Freia força bruta: depois da cota, nem chega a verificar a senha.
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.All(statuses[..3], s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }
}
