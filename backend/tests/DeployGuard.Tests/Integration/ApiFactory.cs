using DeployGuard.Api.Data;
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

    protected virtual int WriteRequestsPerMinute => 1_000;

    public async Task InitializeAsync()
    {
        var baseConnection = Environment.GetEnvironmentVariable("DEPLOYGUARD_TEST_CONNECTION");
        if (string.IsNullOrEmpty(baseConnection))
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
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
        builder.UseSetting("Monitoring:MaxApplications", "5");
        builder.UseSetting("RateLimiting:WriteRequestsPerMinute", WriteRequestsPerMinute.ToString());
        builder.UseSetting("RateLimiting:ReadRequestsPerMinute", "1000");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHealthChecker>();
            services.AddSingleton<IHealthChecker>(HealthChecker);
        });
    }

    public async Task WithDbAsync(Func<DeployGuardDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<DeployGuardDbContext>());
    }

    public HealthCheckScheduler Scheduler => Services.GetRequiredService<HealthCheckScheduler>();

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

public sealed class StrictRateLimitApiFactory : ApiFactory
{
    protected override int WriteRequestsPerMinute => 2;
}
