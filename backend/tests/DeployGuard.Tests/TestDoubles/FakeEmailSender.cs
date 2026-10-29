using System.Collections.Concurrent;
using DeployGuard.Api.Email;

namespace DeployGuard.Tests.TestDoubles;

/// <summary>Guarda os e-mails em memória em vez de falar com um servidor SMTP.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>O envio acontece em segundo plano: espera o e-mail chegar (ou falha após o prazo).</summary>
    public async Task<EmailMessage> WaitForAsync(string to, int alreadySeen = 0, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var match = Sent.Where(m => m.To == to).Skip(alreadySeen).FirstOrDefault();
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Nenhum e-mail novo para {to}.");
    }

    public int CountTo(string to) => Sent.Count(m => m.To == to);
}
