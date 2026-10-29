using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class AccountSecurityTests(AccountSecurityApiFactory factory) : IClassFixture<AccountSecurityApiFactory>
{
    private const string Password = "uma-senha-bem-comprida";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _admin = factory.CreateAdminClient();

    private async Task<UserResponse> CreateUserAsync(string email, UserRole role = UserRole.Viewer)
    {
        var response = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, Password, role), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    /// <summary>Um "navegador" novo: guarda o cookie que o login devolver.</summary>
    private async Task<(HttpClient Client, HttpResponseMessage Response)> LoginAsync(string email, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        return (client, response);
    }

    private async Task<List<AuditEventResponse>> AuditAsync(AuditAction? action = null)
    {
        var page = await _admin.GetFromJsonAsync<AuditPage>($"/api/audit?limit=200{(action is null ? "" : $"&action={action}")}", Json);
        return [.. page!.Items];
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_until_an_admin_unlocks_it()
    {
        var user = await CreateUserAsync("bloqueio@example.com");

        for (var i = 0; i < 5; i++)
        {
            var (_, wrong) = await LoginAsync(user.Email, "senha-errada-123456");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Agora nem a senha certa entra.
        var (_, locked) = await LoginAsync(user.Email, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        var listed = (await _admin.GetFromJsonAsync<UserResponse[]>("/api/users", Json))!.Single(u => u.Id == user.Id);
        Assert.NotNull(listed.LockedUntil);
        Assert.Contains(await AuditAsync(AuditAction.AccountLocked), e => e.TargetId == user.Id.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.PostAsync($"/api/users/{user.Id}/unlock", null)).StatusCode);
        var (_, afterUnlock) = await LoginAsync(user.Email, Password);
        Assert.Equal(HttpStatusCode.OK, afterUnlock.StatusCode);
        Assert.Contains(await AuditAsync(AuditAction.UserUnlocked), e => e.TargetId == user.Id.ToString());
    }

    [Fact]
    public async Task Lockout_ends_by_itself_and_a_good_login_resets_the_counter()
    {
        var user = await CreateUserAsync("expira@example.com");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddSeconds(-1)) // bloqueio que já venceu
            .SetProperty(u => u.FailedLoginCount, 3)));

        var (_, response) = await LoginAsync(user.Email, Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.WithDbAsync(async db =>
        {
            var stored = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
            Assert.Equal(0, stored.FailedLoginCount);
            Assert.Null(stored.LockoutEnd);
        });
    }

    [Fact]
    public async Task Simultaneous_wrong_attempts_are_all_counted()
    {
        var user = await CreateUserAsync("paralelo@example.com");

        // 5 tentativas ao mesmo tempo (como um robô, ou duas instâncias da API): o UPDATE atômico
        // conta todas. Ler, somar e gravar em passos separados deixaria algumas "se perderem".
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => LoginAsync(user.Email, "senha-errada-123456")));

        var (_, response) = await LoginAsync(user.Email, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_email_wrong_password_and_locked_account_look_the_same()
    {
        var user = await CreateUserAsync("igual@example.com");
        await factory.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(10))));

        async Task<(HttpStatusCode, string?, string?)> Attempt(string email, string password)
        {
            var (_, response) = await LoginAsync(email, password);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
            return (response.StatusCode, problem!.Title, problem.Detail);
        }

        var unknown = await Attempt("ninguem@example.com", Password);
        var locked = await Attempt(user.Email, Password);
        var (_, wrongResponse) = await LoginAsync(ApiFactory.AdminEmail, "senha-errada-123456");
        var wrongProblem = await wrongResponse.Content.ReadFromJsonAsync<ProblemDetails>(Json);

        Assert.Equal(unknown, locked);
        Assert.Equal(unknown, (wrongResponse.StatusCode, wrongProblem!.Title, wrongProblem.Detail));
    }

    [Fact]
    public async Task Changing_the_password_signs_out_other_sessions_but_keeps_this_one()
    {
        var user = await CreateUserAsync("troca@example.com");
        var (here, _) = await LoginAsync(user.Email, Password);
        var (otherBrowser, _) = await LoginAsync(user.Email, Password);
        const string newPassword = "outra-senha-mais-comprida";

        var wrongCurrent = await here.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest("nao-e-essa-senha", newPassword));
        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);

        var same = await here.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(Password, Password));
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        var tooShort = await here.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(Password, "curta"));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var changed = await here.PostAsJsonAsync("/api/auth/password", new ChangePasswordRequest(Password, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Contains(changed.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{AuthOptions.CookieName}="));

        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/auth/me")).StatusCode);              // esta sessão segue
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode); // a outra caiu

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, Password)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, newPassword)).Response.StatusCode);
        Assert.Contains(await AuditAsync(AuditAction.PasswordChanged), e => e.ActorEmail == user.Email);
    }

    [Fact]
    public async Task Audit_log_records_who_did_what_and_never_stores_passwords()
    {
        var created = await _admin.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("Auditada", "https://audit.example.com/health", 60));
        var app = (await created.Content.ReadFromJsonAsync<ApplicationResponse>(Json))!;
        await _admin.PutAsJsonAsync($"/api/applications/{app.Id}", new UpdateApplicationRequest(null, null, 120, false));
        await _admin.DeleteAsync($"/api/applications/{app.Id}");
        await LoginAsync(ApiFactory.AdminEmail, "senha-errada-que-nao-pode-vazar");

        var events = await AuditAsync();
        var mine = events.Where(e => e.TargetId == app.Id.ToString()).Select(e => e.Action).ToList();
        Assert.Equal([AuditAction.ApplicationDeleted, AuditAction.ApplicationUpdated, AuditAction.ApplicationCreated], mine);
        var update = events.Single(e => e.Action == AuditAction.ApplicationUpdated && e.TargetId == app.Id.ToString());
        Assert.Contains("CheckIntervalSeconds: 60 → 120", update.Details);
        Assert.All(events.Where(e => e.TargetId == app.Id.ToString()), e => Assert.Equal(ApiFactory.AdminEmail, e.ActorEmail));

        var raw = await _admin.GetStringAsync("/api/audit?limit=200");
        Assert.DoesNotContain("senha-errada-que-nao-pode-vazar", raw);
        Assert.DoesNotContain(ApiFactory.AdminPassword, raw);
    }

    [Fact]
    public async Task Audit_log_is_admin_only_and_paginates()
    {
        var viewer = await CreateUserAsync("leitor-auditoria@example.com");
        var (viewerClient, _) = await LoginAsync(viewer.Email, Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync("/api/audit")).StatusCode);

        var first = (await _admin.GetFromJsonAsync<AuditPage>("/api/audit?limit=2", Json))!;
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextBefore);
        var second = (await _admin.GetFromJsonAsync<AuditPage>($"/api/audit?limit=2&before={first.NextBefore}", Json))!;
        Assert.All(second.Items, e => Assert.True(e.Id < first.Items[^1].Id));
    }
}
