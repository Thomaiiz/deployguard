import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { Location } from '@angular/common';
import { of, throwError } from 'rxjs';
import { DeployGuardApi } from '../core/deployguard-api';
import { ForgotPassword } from './forgot-password';
import { ResetPassword, tokenFromFragment } from './reset-password';

const TOKEN = 'abcdefghijklmnopqrstuvwxyz0123456789_-ABCDE';

function type(element: HTMLElement, values: string[]) {
  element.querySelectorAll('input').forEach((input, i) => {
    input.value = values[i];
    input.dispatchEvent(new Event('input'));
  });
  element.querySelector('form')!.dispatchEvent(new Event('submit'));
}

describe('tokenFromFragment', () => {
  it('reads a well-formed token and rejects anything else', () => {
    expect(tokenFromFragment(`token=${TOKEN}`)).toBe(TOKEN);
    expect(tokenFromFragment(null)).toBeNull();
    expect(tokenFromFragment('token=curto')).toBeNull();
    expect(tokenFromFragment('token=<script>alert(1)</script>xxxxxxxxxxxx')).toBeNull();
  });
});

describe('ForgotPassword', () => {
  async function setup(forgotPassword: DeployGuardApi['forgotPassword']) {
    TestBed.configureTestingModule({
      imports: [ForgotPassword],
      providers: [provideRouter([]), { provide: DeployGuardApi, useValue: { forgotPassword } }],
    });
    const fixture = TestBed.createComponent(ForgotPassword);
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('shows the same neutral message the API returns', async () => {
    const forgotPassword = vi.fn(() => of({ message: 'Se o e-mail estiver cadastrado, ...' }));
    const { fixture, element } = await setup(forgotPassword);

    type(element, ['ana@example.com']);
    await fixture.whenStable();

    expect(forgotPassword).toHaveBeenCalledWith('ana@example.com');
    expect(element.querySelector('[data-testid=forgot-sent]')?.textContent).toContain(
      'Se o e-mail estiver cadastrado',
    );
    expect(element.querySelector('form')).toBeNull();
  });

  it('does not call the API with an invalid e-mail', async () => {
    const forgotPassword = vi.fn();
    const { fixture, element } = await setup(forgotPassword);

    type(element, ['nao-e-email']);
    await fixture.whenStable();

    expect(forgotPassword).not.toHaveBeenCalled();
    expect(element.textContent).toContain('Informe um e-mail válido');
  });
});

describe('ResetPassword', () => {
  async function setup(resetPassword: DeployGuardApi['resetPassword'], fragment: string | null) {
    TestBed.configureTestingModule({
      imports: [ResetPassword],
      providers: [
        provideRouter([]),
        { provide: DeployGuardApi, useValue: { resetPassword } },
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
      ],
    });
    const fixture = TestBed.createComponent(ResetPassword);
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('sends the token from the link and removes it from the address bar', async () => {
    const resetPassword = vi.fn(() => of(undefined));
    const { fixture, element } = await setup(resetPassword, `token=${TOKEN}`);

    expect(TestBed.inject(Location).path(true)).toBe('/reset-password');

    type(element, ['senha-nova-bem-comprida', 'senha-nova-bem-comprida']);
    await fixture.whenStable();

    expect(resetPassword).toHaveBeenCalledWith(TOKEN, 'senha-nova-bem-comprida');
    expect(element.querySelector('[data-testid=reset-done]')).not.toBeNull();
  });

  it('checks that both passwords match before calling the API', async () => {
    const resetPassword = vi.fn();
    const { fixture, element } = await setup(resetPassword, `token=${TOKEN}`);

    type(element, ['senha-nova-bem-comprida', 'outra-senha-diferente']);
    await fixture.whenStable();

    expect(resetPassword).not.toHaveBeenCalled();
    expect(element.textContent).toContain('não são iguais');
  });

  it('explains an expired link and offers a new one', async () => {
    const expired = new HttpErrorResponse({
      status: 400,
      error: { title: 'Link inválido ou expirado.', detail: 'Peça um novo link.' },
    });
    const { fixture, element } = await setup(() => throwError(() => expired), `token=${TOKEN}`);

    type(element, ['senha-nova-bem-comprida', 'senha-nova-bem-comprida']);
    await fixture.whenStable();

    expect(element.querySelector('[role=alert]')?.textContent).toContain(
      'Link inválido ou expirado',
    );
    expect(element.textContent).toContain('Pedir um novo link');
  });

  it('says the link is incomplete when there is no token', async () => {
    const { element } = await setup(vi.fn(), null);
    expect(element.querySelector('[data-testid=reset-invalid]')).not.toBeNull();
    expect(element.querySelector('form')).toBeNull();
  });
});
