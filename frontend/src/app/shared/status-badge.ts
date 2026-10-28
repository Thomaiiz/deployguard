import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CheckStatus } from '../core/models';

const LABELS: Record<CheckStatus | 'Pending' | 'Paused', string> = {
  Up: 'Operacional',
  Degraded: 'Degradado',
  Down: 'Fora do ar',
  Pending: 'Aguardando',
  Paused: 'Pausado',
};

@Component({
  selector: 'dg-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="'badge badge--' + key().toLowerCase()">{{
    label()
  }}</span>`,
})
export class StatusBadge {
  readonly status = input<CheckStatus | null>(null);
  readonly enabled = input(true);

  protected readonly key = computed(() =>
    !this.enabled() ? 'Paused' : (this.status() ?? 'Pending'),
  );
  protected readonly label = computed(() => LABELS[this.key()]);
}
