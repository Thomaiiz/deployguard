import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { PublicComponent, PublicStatus } from '../core/models';
import { Status } from './status';

function component(overrides: Partial<PublicComponent> = {}): PublicComponent {
  return {
    name: 'Loja',
    status: 'Operational',
    availabilityPercent: 99.5,
    incidentSince: null,
    days: [
      { date: '2026-10-27', availabilityPercent: null },
      { date: '2026-10-28', availabilityPercent: 90 },
      { date: '2026-10-29', availabilityPercent: 100 },
    ],
    ...overrides,
  };
}

function status(overrides: Partial<PublicStatus> = {}): PublicStatus {
  return {
    title: 'Status da ACME',
    overall: 'Operational',
    generatedAt: new Date().toISOString(),
    historyDays: 3,
    components: [component()],
    ...overrides,
  };
}

async function setup(getPublicStatus: DeployGuardApi['getPublicStatus']) {
  TestBed.configureTestingModule({
    imports: [Status],
    providers: [{ provide: DeployGuardApi, useValue: { getPublicStatus } }],
  });
  const fixture = TestBed.createComponent(Status);
  // A primeira busca começa com timer(0).
  await new Promise((resolve) => setTimeout(resolve));
  await fixture.whenStable();
  return fixture.nativeElement as HTMLElement;
}

describe('Status', () => {
  it('shows the overall banner and one bar per day', async () => {
    const element = await setup(() => of(status()));

    expect(element.querySelector('h1')?.textContent).toContain('Status da ACME');
    expect(element.querySelector('[data-testid=overall]')?.textContent).toContain(
      'funcionando normalmente',
    );
    const bars = [...element.querySelectorAll('.bar')];
    expect(bars.map((b) => b.className)).toEqual([
      'bar bar--empty',
      'bar bar--down',
      'bar bar--up',
    ]);
    expect(bars[1].getAttribute('title')).toContain('90%');
    expect(element.textContent).toContain('99.5% disponível');
  });

  it('highlights an outage with the incident start', async () => {
    const element = await setup(() =>
      of(
        status({
          overall: 'Outage',
          components: [component({ status: 'Outage', incidentSince: new Date().toISOString() })],
        }),
      ),
    );

    expect(element.querySelector('.status-banner--outage')).not.toBeNull();
    expect(element.querySelector('[data-testid=component]')?.textContent).toContain(
      'Fora do ar desde',
    );
  });

  it('shows an empty state when nothing is published', async () => {
    const element = await setup(() => of(status({ components: [] })));
    expect(element.textContent).toContain('Nenhum serviço foi publicado');
  });

  it('shows an error when the API is unreachable', async () => {
    const element = await setup(() => throwError(() => new HttpErrorResponse({ status: 0 })));
    expect(element.querySelector('[role=alert]')?.textContent).toContain('Não foi possível');
  });
});
