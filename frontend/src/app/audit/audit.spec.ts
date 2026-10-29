import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { AuditEvent, AuditPage } from '../core/models';
import { Audit } from './audit';

function event(id: number, overrides: Partial<AuditEvent> = {}): AuditEvent {
  return {
    id,
    at: new Date().toISOString(),
    action: 'ApplicationCreated',
    actorEmail: 'admin@example.com',
    targetType: 'application',
    targetId: 'abc',
    details: 'Loja: https://loja.example.com/health',
    ipAddress: '203.0.113.7',
    ...overrides,
  };
}

async function setup(listAudit: DeployGuardApi['listAudit']) {
  TestBed.configureTestingModule({
    imports: [Audit],
    providers: [{ provide: DeployGuardApi, useValue: { listAudit } }],
  });
  const fixture = TestBed.createComponent(Audit);
  await fixture.whenStable();
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

describe('Audit', () => {
  it('shows events with readable labels and highlights failed logins', async () => {
    const page: AuditPage = {
      items: [event(2, { action: 'LoginFailed', details: 'Senha incorreta.' }), event(1)],
      nextBefore: null,
    };
    const { element } = await setup(vi.fn(() => of(page)));

    const rows = element.querySelectorAll('[data-testid=audit-row]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Login recusado');
    expect(rows[0].classList).toContain('row--warning');
    expect(rows[1].textContent).toContain('Aplicação cadastrada');
    expect(rows[1].textContent).toContain('203.0.113.7');
  });

  it('loads the next page from the cursor', async () => {
    const listAudit = vi.fn((options?: { before?: number }) =>
      of<AuditPage>(
        options?.before === undefined
          ? { items: [event(10)], nextBefore: 10 }
          : { items: [event(9)], nextBefore: null },
      ),
    );
    const { fixture, element } = await setup(listAudit);

    (element.querySelector('.btn--ghost') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(listAudit).toHaveBeenLastCalledWith(expect.objectContaining({ before: 10 }));
    expect(element.querySelectorAll('[data-testid=audit-row]').length).toBe(2);
    expect(element.querySelector('.btn--ghost')).toBeNull();
  });

  it('filters by action', async () => {
    const listAudit = vi.fn(() => of<AuditPage>({ items: [], nextBefore: null }));
    const { fixture, element } = await setup(listAudit);

    const select = element.querySelector('[data-testid=audit-filter]') as HTMLSelectElement;
    select.value = 'AccountLocked';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();

    expect(listAudit).toHaveBeenLastCalledWith(
      expect.objectContaining({ action: 'AccountLocked' }),
    );
    expect(element.textContent).toContain('Nenhum registro.');
  });
});
