namespace DeployGuard.Api.Domain;

/// <summary>
/// Pedido de "esqueci minha senha". O token em si vai só no e-mail: aqui fica apenas o hash
/// SHA-256, como nas chaves de API. Quem lê o banco não consegue usar um link pendente.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Preenchido quando o link é usado ou substituído por um pedido mais novo.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
