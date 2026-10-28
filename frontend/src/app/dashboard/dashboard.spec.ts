import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { DeployGuardApi } from '../core/deployguard-api';
import { MonitoredApplication } from '../core/models';
import { Dashboard } from './dashboard';

function app(overrides: Partial<MonitoredApplication>): MonitoredApplication {
  return {
    id: crypto.randomUUID(),
    name: 'App',
    healthCheckUrl: 'https://app.example.com/health',
    checkIntervalSeconds: 60,
    isEnabled: true,
    createdAt: new Date().toISOString(),
    lastCheckedAt: new Date().toISOString(),
    lastStatus: 'Up',
    lastLatencyMs: 80,
    availability24hPercent: 100,
    ...overrides,
  };
}

async function setup(api: Partial<DeployGuardApi>) {
  TestBed.configureTestingModule({
    imports: [Dashboard],
    providers: [provideRouter([]), { provide: DeployGuardApi, useValue: api }],
  });
  const fixture = TestBed.createComponent(Dashboard);
  // O polling começa com timer(0): espera um ciclo do event loop e a renderização.
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('Dashboard', () => {
  it('renders one card per application and the status summary', async () => {
    const element = await setup({
      listApplications: () =>
        of([
          app({ name: 'Checkout', lastStatus: 'Up' }),
          app({ name: 'Search', lastStatus: 'Down', lastLatencyMs: null }),
          app({ name: 'Billing', lastStatus: 'Degraded' }),
        ]),
    });

    const cards = element.querySelectorAll('[data-testid=app-card]');
    expect(cards.length).toBe(3);
    expect(cards[1].textContent).toContain('Fora do ar');

    const values = [...element.querySelectorAll('.summary__value')].map((v) =>
      v.textContent?.trim(),
    );
    expect(values).toEqual(['3', '1', '1', '1']); // total, operacionais, degradadas, fora do ar
  });

  it('shows an empty state', async () => {
    const element = await setup({ listApplications: () => of([]) });
    expect(element.textContent).toContain('Nenhuma aplicação cadastrada');
  });

  it('shows an error when the API is unreachable', async () => {
    const element = await setup({
      listApplications: () => throwError(() => new HttpErrorResponse({ status: 0 })),
    });
    expect(element.querySelector('[role=alert]')?.textContent).toContain(
      'Não foi possível falar com a API',
    );
  });

  it('escapes HTML coming from the API (XSS)', async () => {
    const element = await setup({
      listApplications: () => of([app({ name: '<img src=x onerror=alert(1)>' })]),
    });
    expect(element.querySelector('img')).toBeNull();
    expect(element.textContent).toContain('<img src=x onerror=alert(1)>');
  });
});
