import { Routes } from '@angular/router';
import { Account } from './account/account';
import { ApiKeys } from './api-keys/api-keys';
import { ApplicationDetail } from './application-detail/application-detail';
import { Audit } from './audit/audit';
import { adminGuard, authGuard, guestGuard } from './core/auth.guard';
import { Dashboard } from './dashboard/dashboard';
import { ForgotPassword } from './password-reset/forgot-password';
import { Login } from './login/login';
import { ResetPassword } from './password-reset/reset-password';
import { Status } from './status/status';
import { Users } from './users/users';

export const routes: Routes = [
  { path: 'login', component: Login, canActivate: [guestGuard], title: 'DeployGuard · Entrar' },
  {
    path: 'forgot-password',
    component: ForgotPassword,
    canActivate: [guestGuard],
    title: 'DeployGuard · Esqueci minha senha',
  },
  // Sem guard: o link do e-mail precisa abrir mesmo com outra sessão aberta no navegador.
  { path: 'reset-password', component: ResetPassword, title: 'DeployGuard · Nova senha' },
  // Pública de propósito: fica fora do grupo com authGuard.
  { path: 'status', component: Status, title: 'Status dos serviços' },
  {
    path: '',
    canActivateChild: [authGuard], // tudo daqui para baixo exige login
    children: [
      { path: '', component: Dashboard, title: 'DeployGuard' },
      { path: 'applications/:id', component: ApplicationDetail, title: 'DeployGuard · Aplicação' },
      {
        path: 'users',
        component: Users,
        canActivate: [adminGuard],
        title: 'DeployGuard · Usuários',
      },
      { path: 'account', component: Account, title: 'DeployGuard · Minha conta' },
      {
        path: 'audit',
        component: Audit,
        canActivate: [adminGuard],
        title: 'DeployGuard · Auditoria',
      },
      {
        path: 'keys',
        component: ApiKeys,
        canActivate: [adminGuard],
        title: 'DeployGuard · Chaves de API',
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
