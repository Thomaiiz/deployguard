namespace DeployGuard.Api.Auth;

public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    /// <summary>
    /// Regra simples e moderna (recomendação do NIST): exigir comprimento em vez de
    /// combinações obrigatórias de símbolos, que levam a senhas previsíveis.
    /// </summary>
    public static string? Validate(string? password, string? email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"A senha deve ter pelo menos {MinLength} caracteres.";
        }

        if (password.Length > MaxLength)
        {
            return $"A senha deve ter no máximo {MaxLength} caracteres.";
        }

        // Só para nomes com 4+ letras: senão "a@x.com" proibiria qualquer senha com a letra "a".
        var name = email?.Split('@')[0] ?? "";
        if (name.Length >= 4 && password.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return "A senha não pode conter o seu e-mail.";
        }

        return null;
    }
}
