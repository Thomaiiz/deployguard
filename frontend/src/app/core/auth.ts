import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, firstValueFrom, map, of, tap } from 'rxjs';
import { CurrentUser } from './models';

/**
 * Estado da sessão no frontend.
 *
 * O token fica num cookie HttpOnly: o JavaScript NÃO consegue lê-lo (proteção contra XSS).
 * Por isso o frontend descobre "quem sou eu" perguntando à API (/api/auth/me), e o
 * navegador anexa o cookie sozinho em cada requisição para /api.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private session: Promise<CurrentUser | null> | null = null;

  readonly user = signal<CurrentUser | null>(null);
  readonly isAdmin = computed(() => this.user()?.role === 'Admin');

  /** Carrega a sessão uma vez (ex.: ao abrir a página com F5) e reaproveita o resultado. */
  ensureSession(): Promise<CurrentUser | null> {
    this.session ??= firstValueFrom(
      this.http.get<CurrentUser>('/api/auth/me').pipe(
        catchError(() => of(null)), // 401 = não logado
        tap((user) => this.user.set(user)),
      ),
    );
    return this.session;
  }

  login(email: string, password: string): Observable<CurrentUser> {
    return this.http.post<CurrentUser>('/api/auth/login', { email, password }).pipe(
      tap((user) => {
        this.user.set(user);
        this.session = Promise.resolve(user);
      }),
    );
  }

  logout(): Observable<void> {
    return this.http.post<void>('/api/auth/logout', null).pipe(
      catchError(() => of(undefined)), // mesmo se a API falhar, a sessão local é encerrada
      map(() => this.clear()),
    );
  }

  /** Esquece a sessão local (logout ou token expirado). */
  clear(): void {
    this.user.set(null);
    this.session = Promise.resolve(null);
  }
}

/**
 * Só aceita caminhos internos como destino depois do login. Sem isso, um link
 * como /login?returnUrl=https://site-malicioso.com viraria um "open redirect".
 */
export function safeReturnUrl(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\')) {
    return '/';
  }
  return value;
}
