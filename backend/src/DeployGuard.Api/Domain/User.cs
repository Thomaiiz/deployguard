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
}
