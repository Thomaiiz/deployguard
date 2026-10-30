namespace DeployGuard.Api.Domain;

/// <summary>
/// Tentativas de login erradas de um IP numa conta. O bloqueio vale só para esse par:
/// quem erra a senha de propósito a partir de um IP não tranca o dono da conta, que entra de
/// outro lugar. Para ataques espalhados por muitos IPs existe um teto por conta, mais alto,
/// nas colunas <see cref="User.FailedLoginCount"/> e <see cref="User.LockoutEnd"/>.
/// </summary>
public sealed class LoginFailure
{
    public Guid UserId { get; set; }

    public required string IpAddress { get; set; }

    /// <summary>Tentativas erradas seguidas deste IP. Zera no login certo ou quando o par é bloqueado.</summary>
    public int FailedCount { get; set; }

    /// <summary>Enquanto estiver no futuro, este IP não entra nesta conta, nem com a senha certa.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Última tentativa errada. Usado para apagar registros antigos.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
