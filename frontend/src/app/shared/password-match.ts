import { AbstractControl, ValidationErrors } from '@angular/forms';

/** As duas senhas novas precisam ser iguais (só no navegador; a API não recebe a confirmação). */
export function sameNewPassword(group: AbstractControl): ValidationErrors | null {
  const { newPassword, confirmPassword } = group.value as Record<string, string>;
  return confirmPassword && newPassword !== confirmPassword ? { mismatch: true } : null;
}
