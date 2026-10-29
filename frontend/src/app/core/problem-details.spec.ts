import { HttpErrorResponse } from '@angular/common/http';
import { errorMessage, fieldErrors, fullErrorMessage } from './problem-details';

describe('problem details helpers', () => {
  it('extracts field errors from a validation problem', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { title: 'Validation', errors: { healthCheckUrl: ['Apenas https é permitido.'] } },
    });

    expect(fieldErrors(error)).toEqual({ healthCheckUrl: ['Apenas https é permitido.'] });
  });

  it('explains network failures and rate limiting', () => {
    expect(errorMessage(new HttpErrorResponse({ status: 0 }))).toContain('API');
    expect(errorMessage(new HttpErrorResponse({ status: 429 }))).toContain('Muitas requisições');
  });

  it('prefers the problem detail message', () => {
    const error = new HttpErrorResponse({
      status: 409,
      error: { title: 'Limite', detail: 'Máximo 100.' },
    });
    expect(errorMessage(error)).toBe('Máximo 100.');
  });

  it('joins title and detail for messages like a refused login', () => {
    const error = new HttpErrorResponse({
      status: 401,
      error: { title: 'E-mail ou senha inválidos.', detail: 'Depois de 5 tentativas, bloqueia.' },
    });
    expect(fullErrorMessage(error)).toBe(
      'E-mail ou senha inválidos. Depois de 5 tentativas, bloqueia.',
    );
  });
});
