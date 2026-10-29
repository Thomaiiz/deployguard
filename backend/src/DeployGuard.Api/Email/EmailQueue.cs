using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace DeployGuard.Api.Email;

/// <summary>
/// Fila em memória entre a requisição e o envio. Dois motivos:
/// 1. A resposta do "esqueci minha senha" não espera o SMTP. Se esperasse, o tempo de resposta
///    revelaria quais e-mails estão cadastrados (só eles geram envio).
/// 2. O link contém o token em texto puro. Numa fila em memória ele nunca é gravado no banco.
/// Se a instância cair antes do envio, o e-mail se perde e a pessoa pede de novo.
/// </summary>
public sealed class EmailQueue
{
    public const int Capacity = 100;

    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>Não bloqueia: com a fila cheia (um ataque, por exemplo), descarta e devolve false.</summary>
    public bool TryEnqueue(EmailMessage message) => _channel.Writer.TryWrite(message);

    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Esvazia a fila em segundo plano, com algumas tentativas se o SMTP falhar.</summary>
public sealed class EmailDispatcher(
    EmailQueue queue,
    IEmailSender sender,
    IOptionsMonitor<EmailOptions> options,
    TimeProvider time,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    public const int MaxAttempts = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.ReadAllAsync(stoppingToken))
        {
            if (!options.CurrentValue.Enabled)
            {
                // Não registra o destinatário nem o corpo: o corpo tem o link com o token.
                logger.LogWarning("E-mail descartado: SMTP não configurado (Email:SmtpHost).");
                continue;
            }

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await sender.SendAsync(message, stoppingToken);
                    logger.LogInformation("E-mail enviado: {Subject}", message.Subject);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Falha ao enviar e-mail (tentativa {Attempt} de {Max}): {Error}", attempt, MaxAttempts, ex.Message);
                    if (attempt < MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt), time, stoppingToken);
                    }
                }
            }
        }
    }
}

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<EmailQueue>();
        services.AddHostedService<EmailDispatcher>();
        return services;
    }
}
