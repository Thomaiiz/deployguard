import { TestBed } from '@angular/core/testing';
import { HealthCheckResult } from '../core/models';
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
  function render(results: HealthCheckResult[]): HTMLElement {
    const fixture = TestBed.createComponent(LatencyChart);
    fixture.componentRef.setInput('results', results);
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
});
