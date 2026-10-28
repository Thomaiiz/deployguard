namespace DeployGuard.Api.Domain;

/// <summary>
/// Categoria do erro de uma verificação. Guardamos a categoria (e não a mensagem bruta
/// da exceção) para não vazar detalhes internos de rede e para poder agregar erros.
/// </summary>
public enum CheckErrorType
{
    None,
    Timeout,
    DnsFailure,
    ConnectionFailed,
    TlsFailure,
    UnexpectedStatusCode,
    BlockedDestination,
    Unknown,
}
