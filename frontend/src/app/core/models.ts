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
}

export interface CreateUserRequest {
  email: string;
  password: string;
  role: UserRole;
}
