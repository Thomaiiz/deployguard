import { AuditAction } from '../core/models';

/** Nome de cada ação em português, para a tela de auditoria. */
export const AUDIT_LABELS: Record<AuditAction, string> = {
  LoginSucceeded: 'Login',
  LoginFailed: 'Login recusado',
  AccountLocked: 'Conta bloqueada',
  PasswordChanged: 'Senha alterada',
  UserCreated: 'Usuário criado',
  UserDeleted: 'Usuário removido',
  UserUnlocked: 'Conta desbloqueada',
  ApplicationCreated: 'Aplicação cadastrada',
  ApplicationUpdated: 'Aplicação alterada',
  ApplicationDeleted: 'Aplicação removida',
  ApiKeyCreated: 'Chave de API criada',
  ApiKeyRevoked: 'Chave de API revogada',
  PasswordResetRequested: 'Recuperação de senha pedida',
  PasswordResetCompleted: 'Senha redefinida por e-mail',
};

/** Ações que merecem destaque (possível ataque ou mudança de acesso). */
export const AUDIT_WARNINGS = new Set<AuditAction>(['LoginFailed', 'AccountLocked']);
