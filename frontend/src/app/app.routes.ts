import { Routes } from '@angular/router';
import { ApplicationDetail } from './application-detail/application-detail';
import { Dashboard } from './dashboard/dashboard';

export const routes: Routes = [
  { path: '', component: Dashboard, title: 'DeployGuard' },
  { path: 'applications/:id', component: ApplicationDetail, title: 'DeployGuard · Aplicação' },
  { path: '**', redirectTo: '' },
];
