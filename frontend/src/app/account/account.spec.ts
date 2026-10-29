import { signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { Account } from './account';

async function setup(api: Partial<DeployGuardApi>) {
  TestBed.configureTestingModule({
    imports: [Account],
    providers: [
      { provide: DeployGuardApi, useValue: api },
      {
        provide: AuthService,
        useValue: { user: signal({ id: '1', email: 'eu@example.com', role: 'Viewer' }) },
      },
    ],
  });
  const fixture = TestBed.createComponent(Account);
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;

  const fill = async (current: string, next: string, confirm: string) => {
    const inputs = element.querySelectorAll('input');
    [current, next, confirm].forEach((value, i) => {
      inputs[i].value = value;
      inputs[i].dispatchEvent(new Event('input'));
      inputs[i].dispatchEvent(new Event('blur'));
    });
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  };
  return { element, fill };
}

describe('Account', () => {
  it('does not call the API when the new passwords differ', async () => {
    const changePassword = vi.fn(() => of(undefined));
    const { element, fill } = await setup({ changePassword });

    await fill('senha-atual-antiga', 'nova-senha-comprida', 'outra-coisa-diferente');

    expect(changePassword).not.toHaveBeenCalled();
    expect(element.textContent).toContain('As duas senhas novas não são iguais.');
  });

  it('sends only current and new password and confirms success', async () => {
    const changePassword = vi.fn(() => of(undefined));
    const { element, fill } = await setup({ changePassword });

    await fill('senha-atual-antiga', 'nova-senha-comprida', 'nova-senha-comprida');

    expect(changePassword).toHaveBeenCalledWith({
      currentPassword: 'senha-atual-antiga',
      newPassword: 'nova-senha-comprida',
    });
    expect(element.querySelector('[data-testid=password-saved]')).not.toBeNull();
  });

  it('shows the API error next to the field', async () => {
    const changePassword = vi.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: { errors: { currentPassword: ['Senha atual incorreta.'] } },
          }),
      ),
    );
    const { element, fill } = await setup({ changePassword });

    await fill('senha-errada-qualquer', 'nova-senha-comprida', 'nova-senha-comprida');

    expect(element.textContent).toContain('Senha atual incorreta.');
    expect(element.querySelector('[data-testid=password-saved]')).toBeNull();
  });
});
