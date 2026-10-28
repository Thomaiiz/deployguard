import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { EMPTY, Subject, catchError, merge, switchMap, timer } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { MonitoredApplication } from '../core/models';
import { errorMessage } from '../core/problem-details';
import { RelativeTimePipe } from '../shared/relative-time.pipe';
import { StatusBadge } from '../shared/status-badge';
import { RegisterApplicationForm } from './register-application-form';

export const DASHBOARD_REFRESH_MS = 15_000;

@Component({
  selector: 'dg-dashboard',
  imports: [RouterLink, StatusBadge, RelativeTimePipe, RegisterApplicationForm],
  templateUrl: './dashboard.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Dashboard {
  private readonly api = inject(DeployGuardApi);
  private readonly refresh$ = new Subject<void>();

  // Signals: valores reativos. Quando mudam, o Angular atualiza só o que depende deles.
  protected readonly applications = signal<MonitoredApplication[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<string | null>(null);
  protected readonly now = signal(Date.now());
  protected readonly showForm = signal(false);

  // computed: derivado das applications, recalculado automaticamente.
  protected readonly summary = computed(() => {
    const apps = this.applications();
    const active = apps.filter((a) => a.isEnabled);
    return {
      total: apps.length,
      up: active.filter((a) => a.lastStatus === 'Up').length,
      degraded: active.filter((a) => a.lastStatus === 'Degraded').length,
      down: active.filter((a) => a.lastStatus === 'Down').length,
      pending: active.filter((a) => a.lastStatus === null).length,
    };
  });

  constructor() {
    // Polling: a cada 15s (ou quando pedirmos refresh) busca o estado atual.
    // switchMap cancela a requisição anterior se ela ainda estiver pendente,
    // evitando respostas fora de ordem. takeUntilDestroyed encerra ao sair da tela.
    merge(timer(0, DASHBOARD_REFRESH_MS), this.refresh$)
      .pipe(
        switchMap(() =>
          this.api.listApplications().pipe(
            catchError((error) => {
              this.loadError.set(errorMessage(error));
              this.loading.set(false);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((apps) => {
        this.applications.set(apps);
        this.loadError.set(null);
        this.loading.set(false);
        this.now.set(Date.now());
      });
  }

  protected refresh(): void {
    this.refresh$.next();
  }

  protected onRegistered(): void {
    this.showForm.set(false);
    this.refresh();
  }
}
