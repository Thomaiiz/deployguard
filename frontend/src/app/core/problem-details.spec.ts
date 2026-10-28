import { HttpErrorResponse } from '@angular/common/http';
import { errorMessage, fieldErrors } from './problem-details';

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
});
