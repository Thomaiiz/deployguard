using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class PasswordResetTests(PasswordResetApiFactory factory) : IClassFixture<PasswordResetApiFactory>
{
    private const string Password = "uma-senha-bem-comprida";
    private const string NewPassword = "outra-senha-bem-comprida-2026";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _admin = factory.CreateAdminClient();

    private async Task<UserResponse> CreateUserAsync(string email)
    {
        var response = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, Password, UserRole.Viewer), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    private Task<HttpResponseMessage> ForgotAsync(string email) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest(email));

    private Task<HttpResponseMessage> ResetAsync(string token, string newPassword = NewPassword) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordRequest(token, newPassword));

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

    /// <summary>Pede o link e devolve o token tirado do e-mail, como a pessoa faria.</summary>
    private async Task<string> RequestTokenAsync(string email)
    {
        var seen = factory.EmailSender.CountTo(email);
        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(email)).StatusCode);
        var message = await factory.EmailSender.WaitForAsync(email, seen);
        return message.Body.Split("#token=")[1].Split(['\r', '\n'])[0];
    }

    [Fact]
    public async Task Known_and_unknown_emails_get_exactly_the_same_answer()
    {
        var user = await CreateUserAsync("existe@example.com");

        var known = await ForgotAsync(user.Email);
        var unknown = await ForgotAsync("nao-existe@example.com");

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        await factory.EmailSender.WaitForAsync(user.Email);
        Assert.Equal(0, factory.EmailSender.CountTo("nao-existe@example.com"));
    }

    [Fact]
    public async Task The_emailed_link_resets_the_password_once_and_ends_every_session()
    {
        var user = await CreateUserAsync("reset@example.com");
        var oldSession = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await oldSession.PostAsJsonAsync("/api/auth/login", new LoginRequest(user.Email, Password))).StatusCode);

        var token = await RequestTokenAsync(user.Email);
        var email = factory.EmailSender.Sent.Last(m => m.To == user.Email);
        Assert.StartsWith($"{ApiFactory.DashboardBaseUrl}/reset-password#token=", email.Body.Split('\n').Single(l => l.Contains("#token=")).Trim());

        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/auth/me")).StatusCode);

        // O mesmo link não funciona duas vezes.
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "terceira-senha-bem-comprida")).StatusCode);

        var audit = await _admin.GetFromJsonAsync<AuditPage>("/api/audit?limit=200", Json);
        Assert.Contains(audit!.Items, e => e.Action == AuditAction.PasswordResetRequested && e.TargetId == user.Id.ToString());
        Assert.Contains(audit.Items, e => e.Action == AuditAction.PasswordResetCompleted && e.TargetId == user.Id.ToString());
        Assert.DoesNotContain(audit.Items, e => e.Details?.Contains(token) == true);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored()
    {
        var user = await CreateUserAsync("hash@example.com");
        var token = await RequestTokenAsync(user.Email);

        await factory.WithDbAsync(async db =>
        {
            var stored = await db.PasswordResetTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id);
            Assert.NotEqual(token, stored.TokenHash);
            Assert.Equal(PasswordResetEndpoints.Hash(token), stored.TokenHash);
        });
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var user = await CreateUserAsync("expirado@example.com");
        var token = await RequestTokenAsync(user.Email);
        await factory.WithDbAsync(db => db.PasswordResetTokens.Where(t => t.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));

        var response = await ResetAsync(token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, Password)).StatusCode);
    }

    [Fact]
    public async Task A_new_request_cancels_the_previous_link()
    {
        var user = await CreateUserAsync("dois-pedidos@example.com");
        var first = await RequestTokenAsync(user.Email);
        var second = await RequestTokenAsync(user.Email);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(second)).StatusCode);
    }

    [Fact]
    public async Task A_weak_password_does_not_use_up_the_link()
    {
        var user = await CreateUserAsync("fraca@example.com");
        var token = await RequestTokenAsync(user.Email);

        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token, "curta")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);
    }

    [Fact]
    public async Task Requests_per_hour_are_limited_so_nobody_floods_an_inbox()
    {
        var user = await CreateUserAsync("limite@example.com");
        for (var i = 0; i < 3; i++)
        {
            await RequestTokenAsync(user.Email);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await ForgotAsync(user.Email)).StatusCode); // mesma resposta
        await Task.Delay(300);
        Assert.Equal(3, factory.EmailSender.CountTo(user.Email));
    }

    [Fact]
    public async Task Two_simultaneous_resets_with_the_same_link_only_one_wins()
    {
        var user = await CreateUserAsync("corrida@example.com");
        var token = await RequestTokenAsync(user.Email);

        var results = await Task.WhenAll(
            ResetAsync(token, "primeira-senha-concorrente"),
            ResetAsync(token, "segunda-senha-concorrente"));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Resetting_unlocks_a_locked_account()
    {
        var user = await CreateUserAsync("bloqueado-reset@example.com");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(10))));

        var token = await RequestTokenAsync(user.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task The_link_ignores_the_Host_header_of_the_request()
    {
        var user = await CreateUserAsync("host@example.com");
        var seen = factory.EmailSender.CountTo(user.Email);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot-password")
        {
            Content = JsonContent.Create(new ForgotPasswordRequest(user.Email)),
        };
        request.Headers.Host = "site-do-atacante.example.net";
        await client.SendAsync(request);

        var message = await factory.EmailSender.WaitForAsync(user.Email, seen);
        Assert.DoesNotContain("site-do-atacante", message.Body);
        Assert.Contains(ApiFactory.DashboardBaseUrl, message.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token-que-nunca-existiu")]
    public async Task Unknown_tokens_are_refused(string token)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(token)).StatusCode);
    }
}
