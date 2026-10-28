import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HealthCheckResult } from '../core/models';

const WIDTH = 600;
const HEIGHT = 120;
const PADDING = 8;

/** Gráfico de latência em SVG puro: sem biblioteca, fácil de entender e de testar. */
@Component({
  selector: 'dg-latency-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (points().length > 1) {
      <svg
        [attr.viewBox]="'0 0 ' + width + ' ' + height"
        class="chart"
        role="img"
        aria-label="Latência recente"
      >
        <polyline [attr.points]="line()" class="chart__line" />
        @for (p of points(); track p.id) {
          <circle
            [attr.cx]="p.x"
            [attr.cy]="p.y"
            r="3"
            [class]="'chart__dot chart__dot--' + p.status.toLowerCase()"
          >
            <title>{{ p.label }}</title>
          </circle>
        }
      </svg>
      <p class="muted chart__legend">Máx. no período exibido: {{ maxLatency() }} ms</p>
    } @else {
      <p class="muted">Dados insuficientes para o gráfico.</p>
    }
  `,
})
export class LatencyChart {
  readonly results = input.required<HealthCheckResult[]>();

  protected readonly width = WIDTH;
  protected readonly height = HEIGHT;

  protected readonly maxLatency = computed(() =>
    Math.max(1, ...this.results().map((r) => r.latencyMs)),
  );

  protected readonly points = computed(() => {
    // A API devolve do mais novo para o mais antigo; o gráfico lê da esquerda (antigo) para a direita.
    const chronological = [...this.results()].reverse();
    const max = this.maxLatency();
    const step = (WIDTH - PADDING * 2) / Math.max(1, chronological.length - 1);

    return chronological.map((r, i) => ({
      id: r.id,
      status: r.status,
      x: PADDING + i * step,
      y: HEIGHT - PADDING - (r.latencyMs / max) * (HEIGHT - PADDING * 2),
      label: `${new Date(r.checkedAt).toLocaleString()}: ${r.latencyMs} ms (${r.status})`,
    }));
  });

  protected readonly line = computed(() =>
    this.points()
      .map((p) => `${p.x},${p.y}`)
      .join(' '),
  );
}
