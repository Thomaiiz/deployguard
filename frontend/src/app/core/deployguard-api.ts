import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ApplicationStats,
  CreateApplicationRequest,
  CreateUserRequest,
  HealthCheckPage,
  Incident,
  IncidentStatusFilter,
  MonitoredApplication,
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

  listUsers(): Observable<User[]> {
    return this.http.get<User[]>('/api/users');
  }

  createUser(request: CreateUserRequest): Observable<User> {
    return this.http.post<User>('/api/users', request);
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`/api/users/${encodeURIComponent(id)}`);
  }
}
