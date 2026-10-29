import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { ApiKey } from '../core/models';
import { ApiKeys } from './api-keys';

const existing: ApiKey = {
  id: '1',
  name: 'github-actions',
  prefix: 'dg_Ab3x',
  createdAt: new Date().toISOString(),
  createdBy: 'admin@example.com',
  lastUsedAt: null,
};

async function setup(api: Partial<DeployGuardApi>) {
  TestBed.configureTestingModule({
    imports: [ApiKeys],
    providers: [
      { provide: DeployGuardApi, useValue: { listApiKeys: () => of([existing]), ...api } },
    ],
  });
  const fixture = TestBed.createComponent(ApiKeys);
  await fixture.whenStable();
  return { fixture, element: fixture.nativeElement as HTMLElement };
}

describe('ApiKeys', () => {
  it('lists keys showing only the prefix', async () => {
    const { element } = await setup({});
    const row = element.querySelector('[data-testid=api-key-row]');
    expect(row?.textContent).toContain('github-actions');
    expect(row?.textContent).toContain('dg_Ab3x…');
    expect(row?.textContent).toContain('nunca');
  });

  it('shows the full key once, right after creating it', async () => {
    const createApiKey = vi.fn(() =>
      of({ apiKey: { ...existing, id: '2', name: 'nova' }, key: 'dg_chave-completa-secreta' }),
    );
    const { fixture, element } = await setup({ createApiKey });

    const input = element.querySelector('input')!;
    input.value = 'nova';
    input.dispatchEvent(new Event('input'));
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(createApiKey).toHaveBeenCalledWith('nova');
    expect(element.querySelector('[data-testid=new-key]')?.textContent).toContain(
      'dg_chave-completa-secreta',
    );

    (
      [...element.querySelectorAll('button')].find((b) =>
        b.textContent?.includes('Já guardei'),
      ) as HTMLButtonElement
    ).click();
    await fixture.whenStable();
    expect(element.textContent).not.toContain('dg_chave-completa-secreta');
  });
});
