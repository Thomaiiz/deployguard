import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { EMPTY, catchError, combineLatest, switchMap, timer } from 'rxjs';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import {
  ApplicationStats,
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

const REFRESH_MS = 15_000;
const PAGE_SIZE = 50;

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
  ],
  templateUrl: './application-detail.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApplicationDetail {
  private readonly api = inject(DeployGuardApi);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthService);

  /** Preenchido pelo router a partir de /applications/:id (withComponentInputBinding). */
  readonly id = input.required<string>();

  protected readonly app = signal<MonitoredApplication | null>(null);
  protected readonly stats = signal<ApplicationStats | null>(null);
  protected readonly incidents = signal<Incident[]>([]);
  protected readonly checks = signal<HealthCheckResult[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly now = signal(Date.now());
  protected readonly errorLabels = ERROR_LABELS;

  constructor() {
    // Estado e estatísticas atualizam sozinhos; o histórico recarrega sob demanda.
    toObservable(this.id)
      .pipe(
        switchMap((id) =>
          timer(0, REFRESH_MS).pipe(
            switchMap(() =>
              combineLatest([
                this.api.getApplication(id),
                this.api.getStats(id),
                this.api.listApplicationIncidents(id),
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
      .subscribe(([app, stats, incidents]) => {
        this.app.set(app);
        this.stats.set(stats);
        this.incidents.set(incidents);
        this.error.set(null);
        this.now.set(Date.now());
      });

    effect(() => this.reloadHistory(this.id()));
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
