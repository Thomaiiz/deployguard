namespace DeployGuard.Api.Domain;

public enum AuditAction
{
    LoginSucceeded,
    LoginFailed,
    AccountLocked,
    PasswordChanged,
    UserCreated,
    UserDeleted,
    UserUnlocked,
    ApplicationCreated,
    ApplicationUpdated,
    ApplicationDeleted,
    ApiKeyCreated,
    ApiKeyRevoked,
    PasswordResetRequested,
    PasswordResetCompleted,
}

/// <summary>
/// Registro de auditoria: quem fez o quê, quando e de onde. Só é inserido, nunca alterado:
/// a API não tem rota para editar nem apagar estes registros.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    public DateTimeOffset At { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>Usuário que agiu. Nulo quando não se sabe (ex.: login com e-mail inexistente).</summary>
    public Guid? ActorId { get; set; }

    /// <summary>E-mail de quem agiu, guardado como texto: continua legível depois que o usuário é removido.</summary>
    public required string ActorEmail { get; set; }

    /// <summary>"user", "application" ou "api-key".</summary>
    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    /// <summary>Descrição curta e legível. Nunca contém senhas, chaves ou tokens.</summary>
    public string? Details { get; set; }

    public string? IpAddress { get; set; }
}
