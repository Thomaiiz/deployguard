import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { DeployGuardApi } from '../core/deployguard-api';
import { RegisterApplicationForm } from './register-application-form';

async function setup(createApplication: DeployGuardApi['createApplication']) {
  TestBed.configureTestingModule({
    imports: [RegisterApplicationForm],
    providers: [{ provide: DeployGuardApi, useValue: { createApplication } }],
  });
  const fixture = TestBed.createComponent(RegisterApplicationForm);
  await fixture.whenStable();
  const element = fixture.nativeElement as HTMLElement;

  const type = (name: string, value: string) => {
    const input = element.querySelector<HTMLInputElement>(`input[formcontrolname=${name}]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };
  const submit = async () => {
    element.querySelector<HTMLButtonElement>('button[type=submit]')!.click();
    await fixture.whenStable();
  };

  return { fixture, element, type, submit };
}

describe('RegisterApplicationForm', () => {
  it('does not call the API when the form is invalid', async () => {
    const create = vi.fn();
    const { element, submit } = await setup(create);

    await submit();

    expect(create).not.toHaveBeenCalled();
    expect(element.textContent).toContain('Informe um nome');
  });

  it('submits and emits registered on success', async () => {
    const create = vi.fn().mockReturnValue(of({}));
    const { fixture, type, submit } = await setup(create);
    const registered = vi.fn();
    fixture.componentInstance.registered.subscribe(registered);

    type('name', 'Checkout');
    type('healthCheckUrl', 'https://checkout.example.com/health');
    await submit();

    expect(create).toHaveBeenCalledWith({
      name: 'Checkout',
      healthCheckUrl: 'https://checkout.example.com/health',
      checkIntervalSeconds: 60,
    });
    expect(registered).toHaveBeenCalled();
  });

  it('shows validation errors returned by the API', async () => {
    const create = vi.fn().mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 400,
            error: {
              errors: {
                healthCheckUrl: ['Destinos em redes privadas ou locais não são permitidos.'],
              },
            },
          }),
      ),
    );
    const { element, type, submit } = await setup(create);

    type('name', 'Metadata');
    type('healthCheckUrl', 'https://169.254.169.254/');
    await submit();

    expect(element.querySelector('[data-testid=url-error]')?.textContent).toContain(
      'redes privadas',
    );
  });
});
