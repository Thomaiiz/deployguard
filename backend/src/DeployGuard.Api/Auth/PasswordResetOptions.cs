using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Auth;

public sealed class PasswordResetOptions
{
    public const string SectionName = "PasswordReset";

    /// <summary>
    /// Endereço do dashboard usado para montar o link do e-mail (ex.: https://deployguard.empresa.com).
    /// Vem da configuração, NUNCA do cabeçalho Host da requisição: senão um atacante pediria a
    /// redefinição da sua conta com "Host: site-dele.com", e o link do e-mail apontaria para lá
    /// (ataque de host header injection). Vazio = recuperação de senha desligada.
    /// </summary>
    public string? DashboardBaseUrl { get; set; }

    /// <summary>Validade do link. Curta: ele dá acesso à conta para quem tiver o e-mail.</summary>
    [Range(5, 1440)]
    public int TokenLifetimeMinutes { get; set; } = 30;

    /// <summary>Pedidos por usuário por hora. Impede usar o DeployGuard para lotar a caixa de alguém.</summary>
    [Range(1, 20)]
    public int MaxRequestsPerHour { get; set; } = 3;

    public bool Enabled => !string.IsNullOrWhiteSpace(DashboardBaseUrl);

    internal static bool IsValidBaseUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
}
