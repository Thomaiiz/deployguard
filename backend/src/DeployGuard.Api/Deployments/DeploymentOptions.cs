using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.Deployments;

public sealed class DeploymentOptions
{
    public const string SectionName = "Deployments";

    /// <summary>
    /// Um incidente que começa até N minutos depois de um deploy aponta esse deploy como
    /// suspeito. É uma pista para a investigação, não uma prova de causa.
    /// </summary>
    [Range(1, 1440)]
    public int CorrelationWindowMinutes { get; set; } = 30;
}
