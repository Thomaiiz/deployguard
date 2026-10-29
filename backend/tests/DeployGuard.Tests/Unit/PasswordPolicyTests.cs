using DeployGuard.Api.Auth;

namespace DeployGuard.Tests.Unit;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("frase longa e facil de lembrar", "ana@example.com")]
    [InlineData("123456789012", null)]
    [InlineData("a casa amarela da esquina", "a@example.com")] // nome curto demais para a regra do e-mail
    public void Accepts_long_passwords(string password, string? email) =>
        Assert.Null(PasswordPolicy.Validate(password, email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curta123")]
    public void Rejects_short_passwords(string? password) =>
        Assert.NotNull(PasswordPolicy.Validate(password, "ana@example.com"));

    [Fact]
    public void Rejects_absurdly_long_passwords() =>
        Assert.NotNull(PasswordPolicy.Validate(new string('a', PasswordPolicy.MaxLength + 1), null));

    [Fact]
    public void Rejects_password_containing_the_email_name() =>
        Assert.NotNull(PasswordPolicy.Validate("minhasenha-MARIA-2026", "maria@example.com"));
}
