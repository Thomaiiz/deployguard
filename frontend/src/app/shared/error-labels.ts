import { CheckErrorType } from '../core/models';

export const ERROR_LABELS: Record<CheckErrorType, string> = {
  None: '—',
  Timeout: 'Timeout',
  DnsFailure: 'Falha de DNS',
  ConnectionFailed: 'Conexão recusada',
  TlsFailure: 'Erro de TLS',
  UnexpectedStatusCode: 'Status HTTP inesperado',
  BlockedDestination: 'Destino bloqueado',
  Unknown: 'Desconhecido',
};
