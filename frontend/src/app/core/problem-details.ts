import { HttpErrorResponse } from '@angular/common/http';
import { ProblemDetails } from './models';

/** Extrai erros de validação por campo de uma resposta Problem Details. */
export function fieldErrors(error: unknown): Record<string, string[]> {
  const problem = asProblem(error);
  return problem?.errors ?? {};
}

/** Mensagem legível para qualquer erro HTTP. */
export function errorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'Não foi possível falar com a API. Ela está rodando?';
    }
    if (error.status === 429) {
      return 'Muitas requisições. Aguarde um pouco e tente de novo.';
    }
    const problem = asProblem(error);
    return problem?.detail ?? problem?.title ?? `Erro HTTP ${error.status}.`;
  }
  return 'Erro inesperado.';
}

function asProblem(error: unknown): ProblemDetails | null {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
    return error.error as ProblemDetails;
  }
  return null;
}
