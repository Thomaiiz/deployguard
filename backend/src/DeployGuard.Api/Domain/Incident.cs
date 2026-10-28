namespace DeployGuard.Api.Domain;

/// <summary>
/// Período em que uma aplicação ficou fora do ar: abre quando as falhas consecutivas
/// atingem o limiar e fecha na primeira verificação bem-sucedida.
/// </summary>
public sealed class Incident
{
    public long Id { get; set; }

    public Guid ApplicationId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Nulo enquanto o incidente está aberto.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Categoria do erro que abriu o incidente.</summary>
    public CheckErrorType Cause { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? Summary { get; set; }

    public List<AlertNotification> Notifications { get; set; } = [];
}
