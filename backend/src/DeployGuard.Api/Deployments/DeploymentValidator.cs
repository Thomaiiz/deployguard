using System.Text.RegularExpressions;

namespace DeployGuard.Api.Deployments;

public static partial class DeploymentValidator
{
    public const int MaxVersionLength = 100;
    public const int MaxDescriptionLength = 500;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    [GeneratedRegex("^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitShaRegex();

    public static Dictionary<string, string[]> Validate(CreateDeploymentRequest request, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();

        var version = request.Version?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            errors["version"] = ["Informe a versão."];
        }
        else if (version.Length > MaxVersionLength || version.Any(char.IsControl))
        {
            errors["version"] = [$"A versão deve ter até {MaxVersionLength} caracteres, sem quebras de linha."];
        }

        if (!string.IsNullOrWhiteSpace(request.CommitSha) && !CommitShaRegex().IsMatch(request.CommitSha.Trim()))
        {
            errors["commitSha"] = ["O commit deve ter de 7 a 40 caracteres hexadecimais."];
        }

        if (request.Description?.Trim().Length > MaxDescriptionLength)
        {
            errors["description"] = [$"A descrição deve ter até {MaxDescriptionLength} caracteres."];
        }

        // O link vira um <a href> no dashboard. Aceitar só https impede "javascript:alert(1)".
        if (!string.IsNullOrWhiteSpace(request.Url)
            && (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || request.Url.Length > 2048))
        {
            errors["url"] = ["O link deve ser uma URL https válida."];
        }

        if (request.DeployedAt is { } at && (at > now + MaxClockSkew || at < now - MaxAge))
        {
            errors["deployedAt"] = ["A data do deploy deve estar entre os últimos 90 dias e agora."];
        }

        return errors;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
