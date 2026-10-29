import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { errorMessage, fieldErrors } from '../core/problem-details';

/** As duas senhas novas precisam ser iguais (só no navegador; a API não recebe a confirmação). */
function sameNewPassword(group: AbstractControl): ValidationErrors | null {
  const { newPassword, confirmPassword } = group.value as Record<string, string>;
  return confirmPassword && newPassword !== confirmPassword ? { mismatch: true } : null;
}

@Component({
  selector: 'dg-account',
  imports: [ReactiveFormsModule],
  templateUrl: './account.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Account {
  private readonly api = inject(DeployGuardApi);
  protected readonly auth = inject(AuthService);

  protected readonly submitting = signal(false);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string[]>>({});

  protected readonly form = inject(NonNullableFormBuilder).group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
      confirmPassword: ['', Validators.required],
    },
    { validators: sameNewPassword },
  );

  protected submit(): void {
    this.saved.set(false);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.submitting.set(true);
    this.serverErrors.set({});
    this.error.set(null);

    this.api.changePassword({ currentPassword, newPassword }).subscribe({
      next: () => {
        this.submitting.set(false);
        this.saved.set(true);
        this.form.reset();
      },
      error: (err) => {
        this.submitting.set(false);
        const errors = fieldErrors(err);
        this.serverErrors.set(errors);
        if (Object.keys(errors).length === 0) {
          this.error.set(errorMessage(err));
        }
      },
    });
  }

  protected showError(field: 'currentPassword' | 'newPassword' | 'confirmPassword'): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }

  protected showMismatch(): boolean {
    return this.form.hasError('mismatch') && this.form.controls.confirmPassword.touched;
  }
}
