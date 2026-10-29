using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Deployments;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Live;
using DeployGuard.Api.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace DeployGuard.Tests.Integration;

public class LiveEventsApiTests(LiveApiFactory factory) : IClassFixture<LiveApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _admin = factory.CreateAdminClient();

    private LiveEventHub Hub => factory.Services.GetRequiredService<LiveEventHub>();

    /// <summary>Lê o fluxo SSE bloco a bloco ("event: x" + "data: {...}" + linha em branco).</summary>
    private sealed class SseReader(StreamReader reader)
    {
        public async Task<(string Event, string? Data)> NextAsync(string? skip = "ping")
        {
            while (true)
            {
                string? name = null, data = null;
                string? line;
                using var cts = new CancellationTokenSource(Timeout);
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cts.Token)))
                {
                    if (line.StartsWith("event: ")) name = line["event: ".Length..];
                    if (line.StartsWith("data: ")) data = line["data: ".Length..];
                }

                if (line is null) throw new EndOfStreamException();
                if (name is not null && name != skip) return (name, data);
            }
        }
    }

    private async Task<(HttpResponseMessage Response, SseReader Reader)> ConnectAsync(HttpClient client)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/events"),
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return (response, new SseReader(new StreamReader(await response.Content.ReadAsStreamAsync())));
    }

    private async Task WaitForSubscribersAsync(int expected)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (Hub.SubscriberCount != expected)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Anonymous_clients_and_api_keys_cannot_subscribe()
    {
        var anonymous = await factory.CreateClient().GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Uma chave de CI só pode registrar deploys: não serve para escutar o painel.
        var keyResponse = await _admin.PostAsJsonAsync("/api/api-keys", new CreateApiKeyRequest("ci-live"));
        var key = (await keyResponse.Content.ReadFromJsonAsync<CreatedApiKeyResponse>(Json))!.Key;
        var ci = factory.CreateClient();
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ci.GetAsync("/api/events")).StatusCode);
    }

    [Fact]
    public async Task Stream_announces_checks_incidents_and_deployments()
    {
        var created = await _admin.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("Ao vivo", "https://api.example.com/health", 30));
        var app = (await created.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;

        var (response, sse) = await ConnectAsync(_admin);
        using var _ = response;
        Assert.Equal("ready", (await sse.NextAsync()).Event);

        // Duas falhas seguidas (limiar 2): uma verificação e depois verificação + incidente aberto.
        factory.HealthChecker.NextOutcome = new HealthCheckOutcome(CheckStatus.Down, 503, 30, CheckErrorType.UnexpectedStatusCode, "HTTP 503");
        await factory.MakeEverythingDueAsync();
        await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);

        var (name, data) = await sse.NextAsync();
        Assert.Equal("check", name);
        var check = JsonSerializer.Deserialize<LiveEvent>(data!, Json)!;
        Assert.Equal(app.Id, check.ApplicationId);
        Assert.Equal(CheckStatus.Down, check.Status);
        Assert.Contains("\"status\":\"Down\"", data); // enum como texto, igual ao resto da API

        await factory.MakeEverythingDueAsync();
        await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);
        Assert.Equal("check", (await sse.NextAsync()).Event);
        Assert.Equal("incident-opened", (await sse.NextAsync()).Event);

        var deploy = await _admin.PostAsJsonAsync($"/api/applications/{app.Id}/deployments",
            new CreateDeploymentRequest("v9.9.9", null, null, null, null));
        deploy.EnsureSuccessStatusCode();
        (name, data) = await sse.NextAsync();
        Assert.Equal("deployment", name);
        Assert.Equal("v9.9.9", JsonSerializer.Deserialize<LiveEvent>(data!, Json)!.Version);

        await _admin.DeleteAsync($"/api/applications/{app.Id}");
    }

    [Fact]
    public async Task Connections_over_the_limit_get_503_and_slots_are_freed_on_disconnect()
    {
        await WaitForSubscribersAsync(0);
        var (first, _) = await ConnectAsync(_admin);
        var (second, _) = await ConnectAsync(_admin);
        await WaitForSubscribersAsync(2);

        var refused = await _admin.GetAsync("/api/events");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        first.Dispose(); // cliente fechou a aba
        await WaitForSubscribersAsync(1);
        var (third, _) = await ConnectAsync(_admin);

        second.Dispose();
        third.Dispose();
        await WaitForSubscribersAsync(0);
    }
}
