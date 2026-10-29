using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Domain;
using DeployGuard.Api.History;
using DeployGuard.Api.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class ApplicationsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateAdminClient();

    private async Task<ApplicationResponse> CreateAsync(string name, string url = "https://api.example.com/health", int? interval = 30)
    {
        var response = await _client.PostAsJsonAsync("/api/applications", new CreateApplicationRequest(name, url, interval));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;
    }

    private async Task DeleteAsync(Guid id) => await _client.DeleteAsync($"/api/applications/{id}");

    [Fact]
    public async Task Create_get_update_and_delete_application()
    {
        var created = await CreateAsync("Checkout");

        Assert.Equal("Checkout", created.Name);
        Assert.True(created.IsEnabled);
        Assert.Null(created.LastStatus);

        var fetched = await _client.GetFromJsonAsync<ApplicationResponse>($"/api/applications/{created.Id}", Json);
        Assert.Equal(created.Id, fetched!.Id);

        var update = await _client.PutAsJsonAsync($"/api/applications/{created.Id}",
            new UpdateApplicationRequest("Checkout v2", null, 120, false));
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<ApplicationResponse>(Json);
        Assert.Equal("Checkout v2", updated!.Name);
        Assert.Equal(120, updated.CheckIntervalSeconds);
        Assert.False(updated.IsEnabled);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/applications/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/applications/{created.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data", "healthCheckUrl")]
    [InlineData("https://intranet.local/health", "healthCheckUrl")]
    [InlineData("http://api.example.com/health", "healthCheckUrl")]
    public async Task Rejects_unsafe_urls_with_validation_problem(string url, string field)
    {
        var response = await _client.PostAsJsonAsync("/api/applications", new CreateApplicationRequest("Bad", url, 30));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Rejects_interval_below_minimum()
    {
        var response = await _client.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("Fast", "https://example.com/health", 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_application_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/applications/{Guid.NewGuid()}/checks")).StatusCode);
    }

    [Fact]
    public async Task Scheduler_records_result_and_updates_current_state()
    {
        var app = await CreateAsync("Payments");
        factory.HealthChecker.NextOutcome = new HealthCheckOutcome(CheckStatus.Down, 503, 87, CheckErrorType.UnexpectedStatusCode, "HTTP 503");

        var executed = await factory.Scheduler.RunDueChecksAsync(CancellationToken.None);
        Assert.True(executed >= 1);

        // Logo em seguida nada está vencido: o próximo check foi agendado para daqui a 30s.
        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Applications.SingleAsync(a => a.Id == app.Id);
            Assert.True(stored.NextCheckAt > DateTimeOffset.UtcNow);
        });

        var current = await _client.GetFromJsonAsync<ApplicationResponse>($"/api/applications/{app.Id}", Json);
        Assert.Equal(CheckStatus.Down, current!.LastStatus);
        Assert.Equal(0, current.Availability24hPercent);

        var page = await _client.GetFromJsonAsync<HealthCheckPage>($"/api/applications/{app.Id}/checks", Json);
        var check = Assert.Single(page!.Items);
        Assert.Equal(503, check.HttpStatusCode);
        Assert.Equal(CheckErrorType.UnexpectedStatusCode, check.ErrorType);

        var stats = await _client.GetFromJsonAsync<ApplicationStatsResponse>($"/api/applications/{app.Id}/stats", Json);
        Assert.Equal(1, stats!.TotalChecks);
        Assert.Equal(1, stats.Errors[CheckErrorType.UnexpectedStatusCode]);

        await DeleteAsync(app.Id);
    }

    [Fact]
    public async Task History_is_paginated_with_cursor_and_ordered_newest_first()
    {
        var app = await CreateAsync("Search");
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);

        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 5; i++)
            {
                db.HealthCheckResults.Add(new HealthCheckResult
                {
                    ApplicationId = app.Id,
                    CheckedAt = start.AddMinutes(i),
                    Status = i == 2 ? CheckStatus.Down : CheckStatus.Up,
                    LatencyMs = 100 + i,
                    ErrorType = CheckErrorType.None,
                });
            }

            await db.SaveChangesAsync();
        });

        var seen = new List<HealthCheckResultResponse>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/applications/{app.Id}/checks?limit=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = await _client.GetFromJsonAsync<HealthCheckPage>(url, Json);
            seen.AddRange(page!.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal([104, 103, 102, 101, 100], seen.Select(s => s.LatencyMs));

        var stats = await _client.GetFromJsonAsync<ApplicationStatsResponse>($"/api/applications/{app.Id}/stats", Json);
        Assert.Equal(80, stats!.AvailabilityPercent);

        var downOnly = await _client.GetFromJsonAsync<HealthCheckPage>($"/api/applications/{app.Id}/checks?status=Down", Json);
        Assert.Single(downOnly!.Items);

        var invalid = await _client.GetAsync($"/api/applications/{app.Id}/checks?cursor=garbage");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await DeleteAsync(app.Id);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync("/api/applications");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }
}
