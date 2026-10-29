import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { AuthService } from '../core/auth';
import { Login } from './login';

async function setup(login: AuthService['login'], returnUrl?: string) {
  TestBed.configureTestingModule({
    imports: [Login],
    providers: [provideRouter([]), { provide: AuthService, useValue: { login } }],
  });
  const fixture = TestBed.createComponent(Login);
  if (returnUrl) {
    fixture.componentRef.setInput('returnUrl', returnUrl);
  }
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;

  const fill = async (email: string, password: string) => {
    const [emailInput, passwordInput] = element.querySelectorAll('input');
    emailInput.value = email;
    emailInput.dispatchEvent(new Event('input'));
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    await fixture.whenStable();
  };
  return { element, fill };
}

describe('Login', () => {
  it('logs in and goes to the requested page', async () => {
    const login = vi.fn(() => of({ id: '1', email: 'a@example.com', role: 'Admin' as const }));
    const { fill } = await setup(login, '/applications/42');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    await fill('a@example.com', 'uma senha qualquer');

    expect(login).toHaveBeenCalledWith('a@example.com', 'uma senha qualquer');
    expect(navigate).toHaveBeenCalledWith('/applications/42');
  });

  it('never redirects to another site after login', async () => {
    const login = vi.fn(() => of({ id: '1', email: 'a@example.com', role: 'Admin' as const }));
    const { fill } = await setup(login, 'https://evil.example.com');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    await fill('a@example.com', 'uma senha qualquer');

    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('shows the API error and clears the password', async () => {
    const login = vi.fn(() =>
      throwError(
        () =>
          new HttpErrorResponse({ status: 401, error: { title: 'E-mail ou senha inválidos.' } }),
      ),
    );
    const { element, fill } = await setup(login);

    await fill('a@example.com', 'senha errada');

    expect(element.querySelector('[role=alert]')?.textContent).toContain(
      'E-mail ou senha inválidos.',
    );
    expect(element.querySelectorAll('input')[1].value).toBe('');
  });

  it('does not call the API with an empty form', async () => {
    const login = vi.fn();
    const { element } = await setup(login);
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    expect(login).not.toHaveBeenCalled();
  });
});
