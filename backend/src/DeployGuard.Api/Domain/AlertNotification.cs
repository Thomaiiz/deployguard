namespace DeployGuard.Api.Domain;

public enum AlertEventType
{
    IncidentOpened,
    IncidentResolved,
}

/// <summary>
/// Uma notificação a ser entregue num canal (padrão "transactional outbox"):
/// é gravada na mesma transação que abre/fecha o incidente e enviada depois por
/// um serviço separado, com retentativas. Assim um webhook fora do ar não faz
/// perder o alerta nem trava as verificações.
/// </summary>
public sealed class AlertNotification
{
    public long Id { get; set; }

    public long IncidentId { get; set; }

    public Incident? Incident { get; set; }

    public AlertEventType EventType { get; set; }

    /// <summary>Nome do canal configurado (ex.: "slack-plantao").</summary>
    public required string Channel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary>Preenchido quando desistimos após o máximo de tentativas.</summary>
    public DateTimeOffset? FailedAt { get; set; }

    public string? LastError { get; set; }
}
