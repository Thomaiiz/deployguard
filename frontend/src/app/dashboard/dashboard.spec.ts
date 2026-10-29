import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { Incident, MonitoredApplication } from '../core/models';
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
    openIncidentSince: null,
    ...overrides,
  };
}

async function setup(api: Partial<DeployGuardApi>, isAdmin = true) {
  TestBed.configureTestingModule({
    imports: [Dashboard],
    providers: [
      provideRouter([]),
      { provide: DeployGuardApi, useValue: { listIncidents: () => of([]), ...api } },
      { provide: AuthService, useValue: { isAdmin: signal(isAdmin) } },
    ],
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

  it('highlights open incidents with a banner and on the card', async () => {
    const down = app({
      name: 'Pagamentos',
      lastStatus: 'Down',
      openIncidentSince: new Date().toISOString(),
    });
    const incident: Incident = {
      id: 1,
      applicationId: down.id,
      applicationName: 'Pagamentos',
      startedAt: new Date().toISOString(),
      resolvedAt: null,
      isOpen: true,
      durationSeconds: 60,
      cause: 'Timeout',
      httpStatusCode: null,
      summary: 'Sem resposta em 5s.',
      notificationsSent: 1,
      notificationsFailed: 0,
      suspectedDeployment: null,
    };

    const element = await setup({
      listApplications: () => of([down]),
      listIncidents: () => of([incident]),
    });

    const banner = element.querySelector('[data-testid=incident-banner]');
    expect(banner?.textContent).toContain('1 incidente aberto');
    expect(banner?.textContent).toContain('Pagamentos');
    expect(element.querySelector('.app-card__incident')?.textContent).toContain('Incidente aberto');
  });

  it('hides the banner when there are no incidents', async () => {
    const element = await setup({ listApplications: () => of([app({})]) });
    expect(element.querySelector('[data-testid=incident-banner]')).toBeNull();
  });

  it('shows the register button only to admins', async () => {
    const asAdmin = await setup({ listApplications: () => of([]) }, true);
    expect(asAdmin.textContent).toContain('Nova aplicação');
    TestBed.resetTestingModule();

    const asViewer = await setup({ listApplications: () => of([]) }, false);
    expect(asViewer.textContent).not.toContain('Nova aplicação');
    expect(asViewer.textContent).toContain('Peça a um administrador');
  });

  it('points to the deploy that happened right before the incident', async () => {
    const down = app({ name: 'Loja', lastStatus: 'Down' });
    const incident: Incident = {
      id: 2,
      applicationId: down.id,
      applicationName: 'Loja',
      startedAt: new Date().toISOString(),
      resolvedAt: null,
      isOpen: true,
      durationSeconds: 30,
      cause: 'UnexpectedStatusCode',
      httpStatusCode: 500,
      summary: 'HTTP 500',
      notificationsSent: 0,
      notificationsFailed: 0,
      suspectedDeployment: {
        id: 7,
        version: 'v2.3.0',
        commitSha: null,
        deployedAt: new Date().toISOString(),
        minutesBeforeIncident: 3,
      },
    };

    const element = await setup({
      listApplications: () => of([down]),
      listIncidents: () => of([incident]),
    });

    expect(element.querySelector('[data-testid=incident-banner]')?.textContent).toContain(
      '3 min após o deploy v2.3.0',
    );
  });
});
