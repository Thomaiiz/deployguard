using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Applications;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DeployGuard.Tests.Integration;

public class AuthApiTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<UserResponse> CreateUserAsync(string email, UserRole role, string password = "uma-senha-bem-comprida")
    {
        var response = await factory.CreateAdminClient().PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, role), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }

    private async Task<User> LoadUserAsync(Guid id)
    {
        User? user = null;
        await factory.WithDbAsync(async db => user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id));
        return user!;
    }

    [Theory]
    [InlineData("/api/applications")]
    [InlineData("/api/incidents")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/users")]
    public async Task Endpoints_require_login(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_stays_public()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_sets_hardened_cookie_and_me_returns_user()
    {
        var client = factory.CreateClient(); // guarda cookies entre requisições, como um navegador
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("  ADMIN@example.com ", ApiFactory.AdminPassword)); // e-mail é normalizado

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{AuthOptions.CookieName}=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api", setCookie, StringComparison.OrdinalIgnoreCase);

        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/auth/me", Json);
        Assert.Equal(ApiFactory.AdminEmail, me!.Email);
        Assert.Equal(UserRole.Admin, me.Role);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(ApiFactory.AdminEmail, "senha-errada-qualquer")]
    [InlineData("ninguem@example.com", ApiFactory.AdminPassword)]
    [InlineData(null, null)]
    public async Task Invalid_credentials_get_the_same_generic_401(string? email, string? password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("E-mail ou senha inválidos.", problem!.Title); // não revela se o e-mail existe
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write()
    {
        var viewer = await CreateUserAsync("viewer@example.com", UserRole.Viewer);
        var client = factory.CreateClientAs(await LoadUserAsync(viewer.Id));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/incidents")).StatusCode);

        var create = await client.PostAsJsonAsync("/api/applications",
            new CreateApplicationRequest("Checkout", "https://api.example.com/health", 30));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Tampered_or_foreign_tokens_are_rejected()
    {
        var client = factory.CreateClient();

        // Token "alg: none" com papel de Admin: sem assinatura, tem que ser recusado.
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"sub":"{{Guid.NewGuid()}}","role":"Admin","iss":"deployguard","aud":"deployguard-dashboard","exp":4102444800}""")}.";
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthOptions.CookieName}={unsigned}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/applications")).StatusCode);
    }

    [Fact]
    public async Task Deleted_user_loses_access_immediately()
    {
        var created = await CreateUserAsync("temporario@example.com", UserRole.Viewer);
        var client = factory.CreateClientAs(await LoadUserAsync(created.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var delete = await factory.CreateAdminClient().DeleteAsync($"/api/users/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // O token ainda não expirou, mas o usuário não existe mais.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Created_user_can_log_in_and_password_is_hashed()
    {
        var created = await CreateUserAsync("novo@example.com", UserRole.Viewer, "frase-secreta-longa");
        Assert.Equal(UserRole.Viewer, created.Role);

        var stored = await LoadUserAsync(created.Id);
        Assert.DoesNotContain("frase-secreta-longa", stored.PasswordHash);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("novo@example.com", "frase-secreta-longa"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Theory]
    [InlineData("nao-e-email", "uma-senha-bem-comprida", "email")]
    [InlineData("curta@example.com", "curta", "password")]
    [InlineData("fulano@example.com", "fulano-1234567890", "password")]
    public async Task Rejects_invalid_users(string email, string password, string field)
    {
        var response = await factory.CreateAdminClient().PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, UserRole.Viewer), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.Contains(field, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Rejects_duplicate_email_and_self_delete()
    {
        var admin = factory.CreateAdminClient();
        var duplicate = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("Admin@Example.com", "uma-senha-bem-comprida", null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var me = await admin.GetFromJsonAsync<CurrentUserResponse>("/api/auth/me", Json);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{me!.Id}")).StatusCode);
    }

    [Fact]
    public async Task Users_list_never_exposes_password_hash()
    {
        var body = await factory.CreateAdminClient().GetStringAsync("/api/users");
        Assert.Contains(ApiFactory.AdminEmail, body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", body); // prefixo do formato de hash do Identity
    }
}
