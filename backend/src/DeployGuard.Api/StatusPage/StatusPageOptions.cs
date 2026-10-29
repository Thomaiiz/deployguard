using System.ComponentModel.DataAnnotations;

namespace DeployGuard.Api.StatusPage;

public sealed class StatusPageOptions
{
    public const string SectionName = "StatusPage";

    /// <summary>Título exibido no topo da página pública.</summary>
    [Required, StringLength(80, MinimumLength = 1)]
    public string Title { get; set; } = "Status dos serviços";

    /// <summary>
    /// Quantos dias de histórico mostrar. Nunca passa da retenção do monitoramento
    /// (Monitoring:ResultRetentionDays): não dá para mostrar dados que já foram apagados.
    /// </summary>
    [Range(1, 90)]
    public int HistoryDays { get; set; } = 90;

    /// <summary>
    /// Por quantos segundos a resposta fica em cache. A página é aberta por qualquer pessoa,
    /// então o cache impede que muitos acessos virem muitas consultas pesadas no banco.
    /// 0 desliga o cache (usado nos testes).
    /// </summary>
    [Range(0, 300)]
    public int CacheSeconds { get; set; } = 30;
}
