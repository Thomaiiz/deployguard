import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth';

/**
 * Se qualquer chamada à API voltar 401 (token expirou ou usuário foi removido),
 * encerra a sessão local e leva para o login. As rotas de /api/auth ficam de fora:
 * um 401 no login é só "senha errada", e o /me é tratado pelo AuthService.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        !request.url.startsWith('/api/auth/')
      ) {
        auth.clear();
        router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
      }
      return throwError(() => error);
    }),
  );
};
