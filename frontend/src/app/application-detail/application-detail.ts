import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import {
  EMPTY,
  auditTime,
  catchError,
  combineLatest,
  filter,
  merge,
  switchMap,
  tap,
  timer,
} from 'rxjs';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { LiveEventsService } from '../core/live-events';
import {
  ApplicationStats,
  Deployment,
  HealthCheckResult,
  Incident,
  MonitoredApplication,
} from '../core/models';
import { errorMessage } from '../core/problem-details';
import { DurationPipe } from '../shared/duration.pipe';
import { ERROR_LABELS } from '../shared/error-labels';
import { RelativeTimePipe } from '../shared/relative-time.pipe';
import { StatusBadge } from '../shared/status-badge';
import { LatencyChart } from './latency-chart';
import { RegisterDeploymentForm } from './register-deployment-form';

/** Atualização de segurança; o normal é o canal ao vivo avisar. */
const REFRESH_MS = 60_000;
const PAGE_SIZE = 50;
const LIVE_BATCH_MS = 500;

@Component({
  selector: 'dg-application-detail',
  imports: [
    RouterLink,
    DatePipe,
    DecimalPipe,
    StatusBadge,
    RelativeTimePipe,
    DurationPipe,
    LatencyChart,
    RegisterDeploymentForm,
  ],
  templateUrl: './application-detail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplicationDetail {
  private readonly api = inject(DeployGuardApi);
  private readonly router = inject(Router);
  private readonly live = inject(LiveEventsService);
  protected readonly auth = inject(AuthService);

  /** Preenchido pelo router a partir de /applications/:id (withComponentInputBinding). */
  readonly id = input.required<string>();

  protected readonly app = signal<MonitoredApplication | null>(null);
  protected readonly stats = signal<ApplicationStats | null>(null);
  protected readonly incidents = signal<Incident[]>([]);
  protected readonly deployments = signal<Deployment[]>([]);
  protected readonly showDeployForm = signal(false);
  protected readonly checks = signal<HealthCheckResult[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly now = signal(Date.now());
  protected readonly errorLabels = ERROR_LABELS;

  constructor() {
    // Estado e estatísticas atualizam ao abrir, a cada aviso ao vivo desta aplicação e,
    // por segurança, a cada minuto. O histórico também recarrega com uma verificação nova,
    // mas só se o usuário estiver na primeira página (não apaga o que ele carregou a mais).
    toObservable(this.id)
      .pipe(
        switchMap((id) =>
          merge(
            timer(0, REFRESH_MS),
            this.live.messages$.pipe(
              filter((m) => m.name === 'ready' || m.event?.applicationId === id),
              tap((m) => {
                if (m.name === 'check' && this.checks().length <= PAGE_SIZE) {
                  this.reloadHistory(id);
                }
              }),
              auditTime(LIVE_BATCH_MS),
            ),
          ).pipe(
            switchMap(() =>
              combineLatest([
                this.api.getApplication(id),
                this.api.getStats(id),
                this.api.listApplicationIncidents(id),
                this.api.listDeployments(id),
              ]).pipe(
                catchError((err) => {
                  this.error.set(errorMessage(err));
                  return EMPTY;
                }),
              ),
            ),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(([app, stats, incidents, deployments]) => {
        this.app.set(app);
        this.stats.set(stats);
        this.incidents.set(incidents);
        this.deployments.set(deployments);
        this.error.set(null);
        this.now.set(Date.now());
      });

    effect(() => this.reloadHistory(this.id()));
  }

  protected onDeploymentRegistered(): void {
    this.showDeployForm.set(false);
    this.api.listDeployments(this.id()).subscribe({
      next: (deployments) => this.deployments.set(deployments),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected reloadHistory(id = this.id()): void {
    this.api.listChecks(id, { limit: PAGE_SIZE }).subscribe({
      next: (page) => {
        this.checks.set(page.items);
        this.nextCursor.set(page.nextCursor);
      },
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (!cursor) {
      return;
    }
    this.api.listChecks(this.id(), { limit: PAGE_SIZE, cursor }).subscribe({
      next: (page) => {
        this.checks.update((current) => [...current, ...page.items]);
        this.nextCursor.set(page.nextCursor);
      },
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected toggleEnabled(app: MonitoredApplication): void {
    this.api.updateApplication(app.id, { isEnabled: !app.isEnabled }).subscribe({
      next: (updated) =>
        this.app.set({ ...app, ...updated, availability24hPercent: app.availability24hPercent }),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected remove(app: MonitoredApplication): void {
    if (!confirm(`Remover "${app.name}" e todo o histórico?`)) {
      return;
    }
    this.api.deleteApplication(app.id).subscribe({
      next: () => this.router.navigate(['/']),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }
}
