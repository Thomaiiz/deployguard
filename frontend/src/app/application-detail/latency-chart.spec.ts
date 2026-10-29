import { TestBed } from '@angular/core/testing';
import { Deployment, HealthCheckResult } from '../core/models';
import { LatencyChart } from './latency-chart';

const result = (
  id: number,
  latencyMs: number,
  status: HealthCheckResult['status'] = 'Up',
): HealthCheckResult => ({
  id,
  checkedAt: new Date(2026, 9, 28, 12, id).toISOString(),
  status,
  httpStatusCode: 200,
  latencyMs,
  errorType: 'None',
  errorMessage: null,
});

describe('LatencyChart', () => {
  function render(results: HealthCheckResult[], deployments: Deployment[] = []): HTMLElement {
    const fixture = TestBed.createComponent(LatencyChart);
    fixture.componentRef.setInput('results', results);
    fixture.componentRef.setInput('deployments', deployments);
    fixture.detectChanges();
    return fixture.nativeElement;
  }

  it('draws one point per result, colored by status', () => {
    const element = render([result(3, 300, 'Down'), result(2, 200), result(1, 100)]);

    expect(element.querySelectorAll('circle').length).toBe(3);
    expect(element.querySelectorAll('.chart__dot--down').length).toBe(1);
    expect(element.textContent).toContain('300 ms');
  });

  it('asks for more data with fewer than two points', () => {
    expect(render([result(1, 100)]).textContent).toContain('Dados insuficientes');
  });

  const deploy = (id: number, minute: number): Deployment => ({
    id,
    applicationId: 'a',
    applicationName: 'App',
    version: `v${id}`,
    commitSha: null,
    description: null,
    url: null,
    deployedAt: new Date(2026, 9, 28, 12, minute).toISOString(),
    registeredBy: 'ci',
  });

  it('marks deploys inside the displayed period, between the right checks', () => {
    // Verificações aos minutos 1, 2 e 3; um deploy no 2:30 e outro fora do período.
    const element = render(
      [result(3, 300), result(2, 200), result(1, 100)],
      [
        { ...deploy(1, 2), deployedAt: new Date(2026, 9, 28, 12, 2, 30).toISOString() },
        deploy(2, 50),
      ],
    );

    const markers = element.querySelectorAll('[data-testid=deploy-marker]');
    expect(markers.length).toBe(1);
    const circles = [...element.querySelectorAll('circle')].map((c) =>
      Number(c.getAttribute('cx')),
    );
    const x = Number(markers[0].getAttribute('x1'));
    expect(x).toBeGreaterThan(circles[1]);
    expect(x).toBeLessThan(circles[2]);
    expect(markers[0].textContent).toContain('Deploy v1');
  });
});
