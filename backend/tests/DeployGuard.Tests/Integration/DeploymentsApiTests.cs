using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Incidents;
using DeployGuard.Api.Monitoring;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class DeploymentsApiTests(DeploymentApiFactory factory) : IClassFixture<DeploymentApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HealthCheckOutcome Down = new(CheckStatus.Down, 503, 30, CheckErrorType.UnexpectedStatusCode, "HTTP 503");

    private readonly HttpClient _admin = factory.CreateAdminClient();

    private async Task<ApplicationResponse> CreateAppAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest(name, "https://api.example.com/health", 30));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;
    }

    private async Task<string> CreateApiKeyAsync(string name)
    {
        var response = await _admin.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json))!.Key;
    }

    private HttpClient CiClient(string key)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task Admin_registers_a_deployment_and_everyone_can_list_it()
    {
        var app = await CreateAppAsync("Loja");

        var response = await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest(" v1.4.2 ", "ABCDEF1234", "Novo checkout", "https://github.com/acme/loja/actions/runs/1", null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DeploymentResponse>(Json))!;
        Assert.Equal("v1.4.2", created.Version);
        Assert.Equal("abcdef1234", created.CommitSha); // normalizado
        Assert.Equal(ApiFactory.AdminEmail, created.RegisteredBy);

        var list = await _admin.GetFromJsonAsync<DeploymentResponse[]>($"/api/applications/{app.Id}/deployments", Json);
        Assert.Equal("Loja", Assert.Single(list!).ApplicationName);
        Assert.Contains((await _admin.GetFromJsonAsync<DeploymentResponse[]>("/api/deployments", Json))!, d => d.Id == created.Id);
    }

    [Fact]
    public async Task Viewer_cannot_register_deployments()
    {
        var app = await CreateAppAsync("Somente leitura");
        await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest("leitor@example.com", "frase-longa-qualquer", UserRole.Viewer), Json);
        User? viewer = null;
        await factory.WithDbAsync(async db => viewer = await db.Users.SingleAsync(u => u.Email == "leitor@example.com"));
        var client = factory.CreateClientAs(viewer!);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/applications/{app.Id}/deployments")).StatusCode);
        var post = await client.PostAsJsonAsync($"/api/applications/{app.Id}/deployments", new CreateDeploymentRequest("v1", null, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Ci_registers_deployments_with_an_api_key_that_works_nowhere_else()
    {
        var app = await CreateAppAsync("API de pedidos");
        var key = await CreateApiKeyAsync("github-actions");
        Assert.StartsWith(ApiKeys.Prefix, key);

        var ci = CiClient(key);
        var response = await ci.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("2026.10.29-1", null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("chave: github-actions", (await response.Content.ReadFromJsonAsync<DeploymentResponse>(Json))!.RegisteredBy);

        // Menor privilégio: a chave só serve para registrar deploy.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/api/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/api/api-keys")).StatusCode);

        // A lista nunca devolve a chave nem o hash, e mostra o último uso.
        var listBody = await _admin.GetStringAsync("/api/api-keys");
        Assert.DoesNotContain(key, listBody);
        Assert.DoesNotContain(ApiKeys.Hash(key), listBody);
        var listed = JsonSerializer.Deserialize<ApiKeyResponse[]>(listBody, Json)!.Single(k => k.Name == "github-actions");
        Assert.NotNull(listed.LastUsedAt);
        Assert.Equal(ApiKeys.DisplayPrefix(key), listed.Prefix);

        // Revogada, para de funcionar na hora.
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/api-keys/{listed.Id}")).StatusCode);
        var afterRevoke = await ci.PostAsJsonAsync($"/api/applications/{app.Id}/deployments", new CreateDeploymentRequest("v2", null, null, null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Theory]
    [InlineData("dg_chave-que-nao-existe")]
    [InlineData("um-jwt-qualquer")]
    public async Task Unknown_keys_are_rejected(string key)
    {
        var app = await CreateAppAsync($"Chave {key.Length}");
        var response = await CiClient(key).PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("v1", null, null, null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("", null, null, 0, "version")]
    [InlineData("v1", "xyz", null, 0, "commitSha")]
    [InlineData("v1", null, "javascript:alert(1)", 0, "url")]
    [InlineData("v1", null, "http://ci.example.com/run/1", 0, "url")]
    [InlineData("v1", null, null, 60, "deployedAt")]
    public async Task Rejects_invalid_deployments(string version, string? sha, string? url, int minutesInFuture, string field)
    {
        var app = (await _admin.GetFromJsonAsync<ApplicationResponse[]>("/api/applications", Json))!.FirstOrDefault()
            ?? await CreateAppAsync("Validação");
        DateTimeOffset? at = minutesInFuture == 0 ? null : DateTimeOffset.UtcNow.AddMinutes(minutesInFuture);

        var response = await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest(version, sha, null, url, at));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Incident_right_after_a_deploy_points_to_it_in_the_api_and_in_the_alert()
    {
        var app = await CreateAppAsync("Pagamentos");
        // Um deploy antigo (fora da janela de 30 min) e um recente.
        await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("v1.0.0", null, null, null, DateTimeOffset.UtcNow.AddHours(-3)));
        await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("v1.1.0", "abc1234", null, null, DateTimeOffset.UtcNow.AddMinutes(-5)));

        factory.HealthChecker.NextOutcome = Down;
        for (var i = 0; i < 2; i++) // FailureThreshold = 2
        {
            await factory.MakeEverythingDueAsync();
            await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);
        }

        var incident = Assert.Single((await _admin.GetFromJsonAsync<IncidentResponse[]>($"/api/applications/{app.Id}/incidents", Json))!);
        Assert.NotNull(incident.SuspectedDeployment);
        Assert.Equal("v1.1.0", incident.SuspectedDeployment.Version);
        Assert.InRange(incident.SuspectedDeployment.MinutesBeforeIncident, 4, 6);

        await factory.Dispatcher.DispatchPendingAsync(CancellationToken.None);
        var alert = Assert.Single(factory.AlertSender.Sent, s => s.Message.ApplicationId == app.Id).Message;
        Assert.Contains("Último deploy: v1.1.0", alert.Text());
    }

    [Fact]
    public async Task Incident_without_recent_deploy_has_no_suspect()
    {
        var app = await CreateAppAsync("Busca");
        await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("v9", null, null, null, DateTimeOffset.UtcNow.AddHours(-2)));

        await factory.WithDbAsync(db => db.Applications
            .Where(a => a.Id == app.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ConsecutiveFailures, 1)));
        factory.HealthChecker.NextOutcome = Down;
        await factory.MakeEverythingDueAsync();
        await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);

        var incident = Assert.Single((await _admin.GetFromJsonAsync<IncidentResponse[]>($"/api/applications/{app.Id}/incidents", Json))!);
        Assert.Null(incident.SuspectedDeployment);
    }
}
