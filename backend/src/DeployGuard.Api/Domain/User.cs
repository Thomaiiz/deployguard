namespace DeployGuard.Api.Domain;

public enum UserRole
{
    /// <summary>Pode cadastrar, alterar e remover aplicações e usuários.</summary>
    Admin,

    /// <summary>Somente leitura: vê o dashboard, o histórico e os incidentes.</summary>
    Viewer,
}

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Sempre minúsculo e sem espaços, para o índice único funcionar.</summary>
    public required string Email { get; set; }

    /// <summary>
    /// Hash da senha (PBKDF2 com salt aleatório, formato do ASP.NET Core Identity).
    /// A senha em si nunca é gravada.
    /// </summary>
    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Tentativas de login erradas seguidas. Zera no login certo ou quando a conta é bloqueada.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>Enquanto estiver no futuro, o login é recusado mesmo com a senha certa.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>
    /// "Carimbo de segurança": vai dentro do token e é conferido a cada requisição.
    /// Trocar a senha gera um carimbo novo, e todos os tokens antigos param de valer na hora.
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnd > now;
}
