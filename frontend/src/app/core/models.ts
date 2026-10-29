// Tipos espelhando os contratos (DTOs) da API ASP.NET Core.

export type CheckStatus = 'Up' | 'Degraded' | 'Down';

export type CheckErrorType =
  | 'None'
  | 'Timeout'
  | 'DnsFailure'
  | 'ConnectionFailed'
  | 'TlsFailure'
  | 'UnexpectedStatusCode'
  | 'BlockedDestination'
  | 'Unknown';

export interface MonitoredApplication {
  id: string;
  name: string;
  healthCheckUrl: string;
  checkIntervalSeconds: number;
  isEnabled: boolean;
  isPublic: boolean;
  createdAt: string;
  lastCheckedAt: string | null;
  lastStatus: CheckStatus | null;
  lastLatencyMs: number | null;
  availability24hPercent: number | null;
  openIncidentSince: string | null;
}

export interface CreateApplicationRequest {
  name: string;
  healthCheckUrl: string;
  checkIntervalSeconds: number | null;
}

export interface UpdateApplicationRequest {
  name?: string;
  healthCheckUrl?: string;
  checkIntervalSeconds?: number;
  isEnabled?: boolean;
  isPublic?: boolean;
}

export interface HealthCheckResult {
  id: number;
  checkedAt: string;
  status: CheckStatus;
  httpStatusCode: number | null;
  latencyMs: number;
  errorType: CheckErrorType;
  errorMessage: string | null;
}

export interface HealthCheckPage {
  items: HealthCheckResult[];
  nextCursor: string | null;
}

export interface ApplicationStats {
  applicationId: string;
  from: string;
  to: string;
  totalChecks: number;
  availabilityPercent: number | null;
  averageLatencyMs: number | null;
  p95LatencyMs: number | null;
  maxLatencyMs: number | null;
  byStatus: Partial<Record<CheckStatus, number>>;
  errors: Partial<Record<CheckErrorType, number>>;
}

/** Corpo de erro RFC 9457 (Problem Details) devolvido pela API. */
export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export interface Incident {
  id: number;
  applicationId: string;
  applicationName: string;
  startedAt: string;
  resolvedAt: string | null;
  isOpen: boolean;
  durationSeconds: number;
  cause: CheckErrorType;
  httpStatusCode: number | null;
  summary: string | null;
  notificationsSent: number;
  notificationsFailed: number;
  suspectedDeployment: SuspectedDeployment | null;
}

/** Deploy que aconteceu pouco antes do incidente: uma pista, não uma prova. */
export interface SuspectedDeployment {
  id: number;
  version: string;
  commitSha: string | null;
  deployedAt: string;
  minutesBeforeIncident: number;
}

export interface Deployment {
  id: number;
  applicationId: string;
  applicationName: string;
  version: string;
  commitSha: string | null;
  description: string | null;
  url: string | null;
  deployedAt: string;
  registeredBy: string;
}

export interface CreateDeploymentRequest {
  version: string;
  commitSha?: string | null;
  description?: string | null;
  url?: string | null;
  deployedAt?: string | null;
}

export interface ApiKey {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  createdBy: string;
  lastUsedAt: string | null;
}

/** Única resposta com a chave completa: ela não pode ser recuperada depois. */
export interface CreatedApiKey {
  apiKey: ApiKey;
  key: string;
}

export type IncidentStatusFilter = 'All' | 'Open' | 'Resolved';

export type UserRole = 'Admin' | 'Viewer';

export interface CurrentUser {
  id: string;
  email: string;
  role: UserRole;
}

export interface User extends CurrentUser {
  createdAt: string;
  lastLoginAt: string | null;
  /** Preenchido só enquanto a conta estiver bloqueada por tentativas erradas. */
  lockedUntil: string | null;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export type AuditAction =
  | 'LoginSucceeded'
  | 'LoginFailed'
  | 'AccountLocked'
  | 'PasswordChanged'
  | 'UserCreated'
  | 'UserDeleted'
  | 'UserUnlocked'
  | 'ApplicationCreated'
  | 'ApplicationUpdated'
  | 'ApplicationDeleted'
  | 'ApiKeyCreated'
  | 'ApiKeyRevoked'
  | 'PasswordResetRequested'
  | 'PasswordResetCompleted';

export interface AuditEvent {
  id: number;
  at: string;
  action: AuditAction;
  actorEmail: string;
  targetType: string | null;
  targetId: string | null;
  details: string | null;
  ipAddress: string | null;
}

export interface AuditPage {
  items: AuditEvent[];
  /** Passe como "before" para buscar a próxima página; null quando acabou. */
  nextBefore: number | null;
}

export interface CreateUserRequest {
  email: string;
  password: string;
  role: UserRole;
}

/** Nome do evento no canal ao vivo (linha "event:" do Server-Sent Events). */
export type LiveEventName =
  'ready' | 'check' | 'incident-opened' | 'incident-resolved' | 'deployment';

/** Aviso curto de que algo mudou. Os dados completos continuam vindo da API. */
export interface LiveEvent {
  type: 'CheckCompleted' | 'IncidentOpened' | 'IncidentResolved' | 'DeploymentRegistered';
  applicationId: string;
  at: string;
  status: CheckStatus | null;
  latencyMs: number | null;
  version: string | null;
}

export interface LiveMessage {
  name: LiveEventName;
  /** null no "ready", que só avisa que a conexão abriu. */
  event: LiveEvent | null;
}

/** Situação de um serviço na página de status pública. */
export type ComponentStatus = 'Operational' | 'Degraded' | 'Outage' | 'Unknown';

export interface DailyAvailability {
  date: string; // yyyy-MM-dd, em UTC
  availabilityPercent: number | null;
}

/** Só o que qualquer visitante pode ver: sem id, URL, latência ou erro. */
export interface PublicComponent {
  name: string;
  status: ComponentStatus;
  availabilityPercent: number | null;
  incidentSince: string | null;
  days: DailyAvailability[];
}

export interface PublicStatus {
  title: string;
  overall: ComponentStatus;
  generatedAt: string;
  historyDays: number;
  components: PublicComponent[];
}
