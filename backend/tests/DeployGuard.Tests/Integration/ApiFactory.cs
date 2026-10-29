using DeployGuard.Api.Alerting;
using DeployGuard.Api.Auth;
using DeployGuard.Api.Data;
using DeployGuard.Api.Domain;
using DeployGuard.Api.Email;
using DeployGuard.Api.Monitoring;
using DeployGuard.Tests.TestDoubles;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DeployGuard.Tests.Integration;

/// <summary>
/// Sobe a API inteira em memória (WebApplicationFactory) contra um PostgreSQL real.
///
/// O banco vem de DEPLOYGUARD_TEST_CONNECTION (usado no CI com um service container)
/// ou, se a variável não existir, de um container descartável via Testcontainers.
/// Cada factory usa um banco com nome aleatório, então testes não interferem entre si.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = "";

    public FakeHealthChecker HealthChecker { get; } = new();

    public FakeAlertSender AlertSender { get; } = new();

    public FakeEmailSender EmailSender { get; } = new();

    public const string DashboardBaseUrl = "https://deployguard.example.com";

    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "frase-secreta-de-teste-2026";

    /// <summary>Conexão do banco desta factory (cada factory tem um banco próprio).</summary>
    public string ConnectionString => _connectionString;

    protected virtual int WriteRequestsPerMinute => 1_000;

    protected virtual int LoginRequestsPerMinute => 1_000;

    protected virtual int MaxApplications => 5;

    protected virtual int PublicRequestsPerMinute => 1_000;

    /// <summary>Sem cache por padrão: cada teste quer ver o estado atual do banco.</summary>
    protected virtual int StatusCacheSeconds => 0;

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("DEPLOYGUARD_TEST_CONNECTION");
        if (string.IsNullOrEmpty(baseConnection))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            baseConnection = _container.GetConnectionString();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = $"deployguard_test_{Guid.NewGuid():N}",
        }.ConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DeployGuard", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Monitoring:SchedulerEnabled", "false");
        builder.UseSetting("Monitoring:AllowedHosts:0", "example.com");
        builder.UseSetting("Monitoring:AllowedHosts:1", "*.example.com");
        builder.UseSetting("Monitoring:MaxApplications", MaxApplications.ToString());
        builder.UseSetting("RateLimiting:WriteRequestsPerMinute", WriteRequestsPerMinute.ToString());
        builder.UseSetting("RateLimiting:ReadRequestsPerMinute", "1000");
        builder.UseSetting("RateLimiting:LoginRequestsPerMinute", LoginRequestsPerMinute.ToString());
        builder.UseSetting("RateLimiting:PublicRequestsPerMinute", PublicRequestsPerMinute.ToString());
        builder.UseSetting("StatusPage:CacheSeconds", StatusCacheSeconds.ToString());
        builder.UseSetting("Auth:JwtSigningKey", "chave-de-teste-com-pelo-menos-32-caracteres");
        builder.UseSetting("Auth:CookieSecure", "false"); // o TestServer usa http://localhost
        builder.UseSetting("Auth:BootstrapAdminEmail", AdminEmail);
        builder.UseSetting("Auth:BootstrapAdminPassword", AdminPassword);
        builder.UseSetting("Email:SmtpHost", "smtp.example.com");
        builder.UseSetting("PasswordReset:DashboardBaseUrl", DashboardBaseUrl);
        builder.UseSetting("Alerting:DispatcherEnabled", "false");
        builder.UseSetting("Alerting:FailureThreshold", "2");
        builder.UseSetting("Alerting:MaxDeliveryAttempts", "2");
        builder.UseSetting("Alerting:Webhooks:0:Name", "plantao");
        builder.UseSetting("Alerting:Webhooks:0:Url", "https://hooks.example.com/services/test");
        builder.UseSetting("Alerting:Webhooks:0:Format", "Slack");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHealthChecker>();
            services.AddSingleton<IHealthChecker>(HealthChecker);
            services.RemoveAll<IAlertSender>();
            services.AddSingleton<IAlertSender>(AlertSender);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(EmailSender);
        });
    }

    public async Task WithDbAsync(Func<DeployGuardDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>());
    }

    /// <summary>
    /// Cliente já autenticado como o usuário informado. Gera o token direto pelo TokenService
    /// (sem passar pelo /login), para não gastar a cota do rate limit de login nos testes.
    /// </summary>
    public HttpClient CreateClientAs(User user)
    {
        var client = CreateClient();
        var (token, _) = Services.GetRequiredService<TokenService>().Create(user);
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthOptions.CookieName}={token}");
        return client;
    }

    public HttpClient CreateAdminClient()
    {
        CreateClient().Dispose(); // garante que a API subiu (migrations + admin inicial)
        using var scope = Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>().Users.Single(u => u.Email == AdminEmail);
        return CreateClientAs(admin);
    }

    public HealthCheckScheduler Scheduler => Services.GetRequiredService<HealthCheckScheduler>();

    public AlertDispatcher Dispatcher => Services.GetRequiredService<AlertDispatcher>();

    /// <summary>Faz todas as aplicações (e alertas pendentes) ficarem "vencidos" agora.</summary>
    public Task MakeEverythingDueAsync() => WithDbAsync(async db =>
    {
        var past = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.Applications.ExecuteUpdateAsync(s => s.SetProperty(a => a.NextCheckAt, past));
        await db.AlertNotifications.ExecuteUpdateAsync(s => s.SetProperty(n => n.NextAttemptAt, past));
    });

    async Task IAsyncLifetime.DisposeAsync()
    {
        await WithDbAsync(db => db.Database.EnsureDeletedAsync());
        await DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public sealed class IncidentApiFactory : ApiFactory;

public sealed class StrictRateLimitApiFactory : ApiFactory
{
    protected override int WriteRequestsPerMinute => 2;

    protected override int LoginRequestsPerMinute => 3;
}

public sealed class AuthApiFactory : ApiFactory;

public sealed class DeploymentApiFactory : ApiFactory
{
    protected override int MaxApplications => 50;
}

public sealed class LiveApiFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Live:MaxConnections", "2");
    }
}

/// <summary>Lotes pequenos (2 verificações x 4 ondas) para várias instâncias dividirem o trabalho.</summary>
public sealed class MultiInstanceApiFactory : ApiFactory
{
    protected override int MaxApplications => 50;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Monitoring:MaxConcurrentChecks", "2");
    }
}

public sealed class AccountSecurityApiFactory : ApiFactory;

public sealed class PasswordResetApiFactory : ApiFactory;

public sealed class StatusPageApiFactory : ApiFactory
{
    protected override int MaxApplications => 20;
}

public sealed class CachedStatusPageApiFactory : ApiFactory
{
    protected override int StatusCacheSeconds => 30;
}

public sealed class StrictStatusPageApiFactory : ApiFactory
{
    protected override int PublicRequestsPerMinute => 3;
}
