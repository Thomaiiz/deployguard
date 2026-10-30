using Testcontainers.Redis;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>
/// Um Redis para os testes: o da variável DEPLOYGUARD_TEST_REDIS (ex.: localhost:6379) ou um
/// container do Testcontainers, criado uma vez e reaproveitado por todos os testes.
/// </summary>
public static class RedisTestServer
{
    private static readonly Lazy<string> Connection = new(() =>
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DEPLOYGUARD_TEST_REDIS");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        var container = new RedisBuilder("redis:8.6-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        return container.GetConnectionString();
    });

    public static string ConnectionString => Connection.Value;
}
