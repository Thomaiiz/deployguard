import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Title } from '@angular/platform-browser';
import { catchError, of, switchMap, timer } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { ComponentStatus, DailyAvailability, PublicStatus } from '../core/models';

/** A API guarda a resposta em cache por 30 s, então atualizar a cada minuto basta. */
export const STATUS_REFRESH_MS = 60_000;

const OVERALL: Record<ComponentStatus, string> = {
  Operational: 'Todos os serviços estão funcionando normalmente.',
  Degraded: 'Alguns serviços estão instáveis.',
  Outage: 'Há serviços fora do ar.',
  Unknown: 'Ainda não há verificações.',
};

const LABELS: Record<ComponentStatus, string> = {
  Operational: 'Operacional',
  Degraded: 'Instável',
  Outage: 'Fora do ar',
  Unknown: 'Sem dados',
};

/**
 * Página pública (sem login). Não usa o canal ao vivo (/api/events), que exige login:
 * atualiza por polling, e a API responde do cache.
 */
@Component({
  selector: 'dg-status',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './status.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Status {
  private readonly api = inject(DeployGuardApi);
  private readonly title = inject(Title);

  protected readonly overall = OVERALL;
  protected readonly labels = LABELS;
  protected readonly status = signal<PublicStatus | null>(null);
  protected readonly failed = signal(false);

  constructor() {
    timer(0, STATUS_REFRESH_MS)
      .pipe(
        switchMap(() => this.api.getPublicStatus().pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe((status) => {
        // Se uma atualização falhar, mantém a última resposta boa na tela.
        this.failed.set(status === null);
        if (status) {
          this.status.set(status);
          this.title.setTitle(status.title);
        }
      });
  }

  protected barClass(day: DailyAvailability): string {
    const percent = day.availabilityPercent;
    if (percent === null) {
      return 'bar bar--empty';
    }
    return percent >= 99 ? 'bar bar--up' : percent >= 95 ? 'bar bar--degraded' : 'bar bar--down';
  }

  protected badgeClass(status: ComponentStatus): string {
    const key = { Operational: 'up', Degraded: 'degraded', Outage: 'down', Unknown: 'pending' }[
      status
    ];
    return `badge badge--${key}`;
  }
}
