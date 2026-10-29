using System.Text.Json;
using System.Text.Json.Serialization;
using DeployGuard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DeployGuard.Api.Live;

/// <summary>
/// Leva os avisos ao vivo para TODAS as instâncias da API usando o LISTEN/NOTIFY do PostgreSQL.
///
/// Quem produz um evento faz NOTIFY no banco. Cada instância mantém uma conexão fazendo LISTEN
/// (<see cref="LiveEventListener"/>) e repassa o que chegar para os navegadores conectados nela.
/// Assim um navegador conectado na instância B recebe o aviso de uma verificação feita pela A,
/// sem precisar de Redis ou de outro serviço: o banco que já temos faz o papel de "carteiro".
/// </summary>
public sealed class LiveEventBus
{
    public const string Channel = "deployguard_live";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Publica pela conexão (e transação, se houver) do DbContext. Dentro de uma transação, o
    /// PostgreSQL só entrega as notificações no COMMIT; num ROLLBACK, elas somem junto.
    /// </summary>
    public async Task PublishAsync(DeployGuardDbContext db, IReadOnlyCollection<LiveEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return;
        }

        // Um aviso tem menos de 300 bytes, longe do limite de 8000 bytes de um NOTIFY.
        // unnest transforma o array em linhas: várias notificações numa ida só ao banco.
        var payloads = events.Select(e => JsonSerializer.Serialize(e, Json)).ToArray();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({Channel}, payload) FROM unnest({payloads}) AS payload", ct);
    }

    internal static LiveEvent? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<LiveEvent>(payload, Json);
        }
        catch (JsonException)
        {
            return null; // qualquer um com acesso ao banco pode dar NOTIFY: ignora o que não entende
        }
    }
}

/// <summary>
/// Mantém uma conexão dedicada ao PostgreSQL fazendo LISTEN e entrega cada aviso ao
/// <see cref="LiveEventHub"/> desta instância. Se a conexão cair, reconecta com espera crescente.
/// </summary>
public sealed class LiveEventListener(
    IConfiguration configuration,
    LiveEventHub hub,
    TimeProvider time,
    ILogger<LiveEventListener> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private volatile bool _listening;

    /// <summary>true enquanto o LISTEN está ativo. Os testes esperam por ele antes de publicar.</summary>
    public bool IsListening => _listening;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Conexão própria, fora do pool do EF: ela fica aberta para sempre esperando avisos.
        // KeepAlive manda um sinal a cada 30 s para perceber logo se a conexão morreu.
        var connectionString = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DeployGuard"))
        {
            KeepAlive = 30,
            Pooling = false,
        }.ConnectionString;

        var retryDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, args) =>
                {
                    if (LiveEventBus.Parse(args.Payload) is { } liveEvent)
                    {
                        hub.Publish(liveEvent);
                    }
                };

                await using (var listen = new NpgsqlCommand($"LISTEN {LiveEventBus.Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                _listening = true;
                retryDelay = TimeSpan.FromSeconds(1);
                logger.LogInformation("Escutando avisos ao vivo no canal {Channel}.", LiveEventBus.Channel);

                while (true)
                {
                    await connection.WaitAsync(stoppingToken); // dorme até chegar uma notificação
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Enquanto isso, os dashboards seguem com a atualização de segurança de 1 minuto.
                _listening = false;
                logger.LogWarning(ex, "Conexão de avisos ao vivo caiu. Nova tentativa em {Delay}.", retryDelay);
                await Task.Delay(retryDelay, time, stoppingToken);
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }
}
