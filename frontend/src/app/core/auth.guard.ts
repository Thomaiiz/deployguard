import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth';

/** Bloqueia rotas para quem não está logado e manda para /login, lembrando o destino. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (await auth.ensureSession()) {
    return true;
  }
  return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/**
 * Esconde telas de administração de quem é Viewer. Isso é só experiência de uso:
 * quem protege de verdade é a API, que responde 403 para quem não é Admin.
 */
export const adminGuard: CanActivateFn = async () => {
  // inject() só funciona antes do primeiro await (fora dele não há contexto de injeção).
  const auth = inject(AuthService);
  const router = inject(Router);
  await auth.ensureSession();
  return auth.isAdmin() ? true : router.createUrlTree(['/']);
};

/** Quem já está logado não precisa ver a tela de login. */
export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return (await auth.ensureSession()) ? router.createUrlTree(['/']) : true;
};
