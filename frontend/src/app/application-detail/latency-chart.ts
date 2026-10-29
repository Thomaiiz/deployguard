import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Deployment, HealthCheckResult } from '../core/models';

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
        @for (m of deployMarkers(); track m.id) {
          <!-- Linha vertical no momento do deploy: queda logo depois dela é a primeira pista. -->
          <line
            [attr.x1]="m.x"
            [attr.x2]="m.x"
            y1="0"
            [attr.y2]="height"
            class="chart__deploy"
            data-testid="deploy-marker"
          >
            <title>{{ m.label }}</title>
          </line>
        }
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
      <p class="muted chart__legend">
        Máx. no período exibido: {{ maxLatency() }} ms
        @if (deployMarkers().length > 0) {
          · <span class="chart__deploy-key">┃</span> deploy
        }
      </p>
    } @else {
      <p class="muted">Dados insuficientes para o gráfico.</p>
    }
  `,
})
export class LatencyChart {
  readonly results = input.required<HealthCheckResult[]>();
  readonly deployments = input<Deployment[]>([]);

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
      time: new Date(r.checkedAt).getTime(),
      x: PADDING + i * step,
      y: HEIGHT - PADDING - (r.latencyMs / max) * (HEIGHT - PADDING * 2),
      label: `${new Date(r.checkedAt).toLocaleString()}: ${r.latencyMs} ms (${r.status})`,
    }));
  });

  /**
   * O eixo X é por ordem das verificações, não por tempo. Para posicionar um deploy,
   * encontramos as duas verificações em volta dele e interpolamos entre elas.
   */
  protected readonly deployMarkers = computed(() => {
    const points = this.points();
    if (points.length < 2) {
      return [];
    }
    const times = points.map((p) => p.time);

    return this.deployments().flatMap((d) => {
      const t = new Date(d.deployedAt).getTime();
      if (t < times[0] || t > times[times.length - 1]) {
        return []; // fora do período exibido
      }
      const i = Math.max(
        0,
        times.findIndex((time) => time >= t),
      );
      const before = points[Math.max(0, i - 1)];
      const after = points[i];
      const span = after.time - before.time;
      const ratio = span === 0 ? 0 : (t - before.time) / span;
      return [
        {
          id: d.id,
          x: before.x + (after.x - before.x) * ratio,
          label: `Deploy ${d.version} em ${new Date(d.deployedAt).toLocaleString()}`,
        },
      ];
    });
  });

  protected readonly line = computed(() =>
    this.points()
      .map((p) => `${p.x},${p.y}`)
      .join(' '),
  );
}
