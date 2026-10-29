import { Routes } from '@angular/router';
import { ApplicationDetail } from './application-detail/application-detail';
import { adminGuard, authGuard, guestGuard } from './core/auth.guard';
import { Dashboard } from './dashboard/dashboard';
import { Login } from './login/login';
import { Users } from './users/users';

export const routes: Routes = [
  { path: 'login', component: Login, canActivate: [guestGuard], title: 'DeployGuard · Entrar' },
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
    ],
  },
  { path: '**', redirectTo: '' },
];
