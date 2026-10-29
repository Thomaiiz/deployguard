import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ApiKey,
  ApplicationStats,
  AuditAction,
  AuditPage,
  ChangePasswordRequest,
  CreatedApiKey,
  CreateApplicationRequest,
  CreateDeploymentRequest,
  CreateUserRequest,
  Deployment,
  HealthCheckPage,
  Incident,
  IncidentStatusFilter,
  MonitoredApplication,
  PublicStatus,
  UpdateApplicationRequest,
  User,
} from './models';

/**
 * Único ponto do frontend que conhece as URLs da API.
 * Usa caminhos relativos (/api/...): em desenvolvimento o proxy do `ng serve`
 * encaminha para o backend; no Docker, o nginx faz o mesmo. Assim o navegador
 * fala com uma só origem e não precisamos liberar CORS.
 */
@Injectable({ providedIn: 'root' })
export class DeployGuardApi {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/applications';

  listApplications(): Observable<MonitoredApplication[]> {
    return this.http.get<MonitoredApplication[]>(this.baseUrl);
  }

  getApplication(id: string): Observable<MonitoredApplication> {
    return this.http.get<MonitoredApplication>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  createApplication(request: CreateApplicationRequest): Observable<MonitoredApplication> {
    return this.http.post<MonitoredApplication>(this.baseUrl, request);
  }

  updateApplication(
    id: string,
    request: UpdateApplicationRequest,
  ): Observable<MonitoredApplication> {
    return this.http.put<MonitoredApplication>(
      `${this.baseUrl}/${encodeURIComponent(id)}`,
      request,
    );
  }

  deleteApplication(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${encodeURIComponent(id)}`);
  }

  listChecks(
    id: string,
    options: { limit?: number; cursor?: string | null } = {},
  ): Observable<HealthCheckPage> {
    let params = new HttpParams().set('limit', options.limit ?? 50);
    if (options.cursor) {
      params = params.set('cursor', options.cursor);
    }
    return this.http.get<HealthCheckPage>(`${this.baseUrl}/${encodeURIComponent(id)}/checks`, {
      params,
    });
  }

  getStats(id: string, hours = 24): Observable<ApplicationStats> {
    return this.http.get<ApplicationStats>(`${this.baseUrl}/${encodeURIComponent(id)}/stats`, {
      params: new HttpParams().set('hours', hours),
    });
  }

  listIncidents(status: IncidentStatusFilter = 'All', limit = 50): Observable<Incident[]> {
    return this.http.get<Incident[]>('/api/incidents', {
      params: new HttpParams().set('status', status).set('limit', limit),
    });
  }

  listApplicationIncidents(id: string, limit = 20): Observable<Incident[]> {
    return this.http.get<Incident[]>(`${this.baseUrl}/${encodeURIComponent(id)}/incidents`, {
      params: new HttpParams().set('limit', limit),
    });
  }

  /** Rota pública: funciona sem login. */
  getPublicStatus(): Observable<PublicStatus> {
    return this.http.get<PublicStatus>('/api/public/status');
  }

  listUsers(): Observable<User[]> {
    return this.http.get<User[]>('/api/users');
  }

  createUser(request: CreateUserRequest): Observable<User> {
    return this.http.post<User>('/api/users', request);
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`/api/users/${encodeURIComponent(id)}`);
  }

  unlockUser(id: string): Observable<void> {
    return this.http.post<void>(`/api/users/${encodeURIComponent(id)}/unlock`, null);
  }

  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.http.post<void>('/api/auth/password', request);
  }

  /** Público. A resposta é sempre a mesma, exista o e-mail ou não. */
  forgotPassword(email: string): Observable<{ message: string }> {
    return this.http.post<{ message: string }>('/api/auth/forgot-password', { email });
  }

  /** Público. O token veio no link do e-mail. */
  resetPassword(token: string, newPassword: string): Observable<void> {
    return this.http.post<void>('/api/auth/reset-password', { token, newPassword });
  }

  listAudit(
    options: { limit?: number; before?: number; action?: AuditAction } = {},
  ): Observable<AuditPage> {
    let params = new HttpParams().set('limit', options.limit ?? 50);
    if (options.before !== undefined) {
      params = params.set('before', options.before);
    }
    if (options.action) {
      params = params.set('action', options.action);
    }
    return this.http.get<AuditPage>('/api/audit', { params });
  }

  listDeployments(applicationId: string, limit = 20): Observable<Deployment[]> {
    return this.http.get<Deployment[]>(
      `${this.baseUrl}/${encodeURIComponent(applicationId)}/deployments`,
      { params: new HttpParams().set('limit', limit) },
    );
  }

  createDeployment(
    applicationId: string,
    request: CreateDeploymentRequest,
  ): Observable<Deployment> {
    return this.http.post<Deployment>(
      `${this.baseUrl}/${encodeURIComponent(applicationId)}/deployments`,
      request,
    );
  }

  listApiKeys(): Observable<ApiKey[]> {
    return this.http.get<ApiKey[]>('/api/api-keys');
  }

  createApiKey(name: string): Observable<CreatedApiKey> {
    return this.http.post<CreatedApiKey>('/api/api-keys', { name });
  }

  deleteApiKey(id: string): Observable<void> {
    return this.http.delete<void>(`/api/api-keys/${encodeURIComponent(id)}`);
  }
}
