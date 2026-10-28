using System.Net;
using System.Text.Json;
using DeployGuard.Api.Alerting;
using DeployGuard.Api.Domain;
using DeployGuard.Tests.TestDoubles;

namespace DeployGuard.Tests.Unit;

public class WebhookAlertSenderTests
{
    private static readonly AlertMessage Message = new(AlertEventType.IncidentOpened, 7, Guid.NewGuid(), "Checkout",
        DateTimeOffset.UtcNow, null, CheckErrorType.Timeout, "Sem resposta.", null);

    private static async Task<(JsonElement Body, StubHttpMessageHandler Handler)> SendAsync(WebhookFormat format, HttpStatusCode reply = HttpStatusCode.OK)
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(reply);
        });
        var sender = new WebhookAlertSender(new StubHttpClientFactory(handler));

        await sender.SendAsync(new WebhookChannelOptions { Name = "c", Url = "https://hooks.example.com/x", Format = format }, Message, CancellationToken.None);
        return (JsonDocument.Parse(body!).RootElement, handler);
    }

    [Fact]
    public async Task Slack_payload_uses_text()
    {
        var (body, _) = await SendAsync(WebhookFormat.Slack);
        Assert.Contains("Checkout", body.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Discord_payload_uses_content()
    {
        var (body, _) = await SendAsync(WebhookFormat.Discord);
        Assert.Contains("FORA DO AR", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Generic_payload_has_structured_fields()
    {
        var (body, _) = await SendAsync(WebhookFormat.Generic);
        Assert.Equal("IncidentOpened", body.GetProperty("event").GetString());
        Assert.Equal(7, body.GetProperty("incidentId").GetInt64());
        Assert.Equal("Timeout", body.GetProperty("cause").GetString());
    }

    [Fact]
    public async Task Non_success_response_throws_without_leaking_the_url()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(WebhookFormat.Slack, HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("hooks.example.com", ex.Message);
    }
}
