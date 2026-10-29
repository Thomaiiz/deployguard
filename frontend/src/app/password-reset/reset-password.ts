import { Location } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DeployGuardApi } from '../core/deployguard-api';
import { fieldErrors, fullErrorMessage } from '../core/problem-details';
import { sameNewPassword } from '../shared/password-match';

/**
 * Lê o token do fragmento da URL (/reset-password#token=...). O fragmento nunca é enviado ao
 * servidor, então o token não fica em logs de acesso. Logo depois de lido, ele sai da barra de
 * endereços e do histórico do navegador.
 */
export function tokenFromFragment(fragment: string | null): string | null {
  const token = new URLSearchParams(fragment ?? '').get('token');
  return token && /^[A-Za-z0-9_-]{20,100}$/.test(token) ? token : null;
}

@Component({
  selector: 'dg-reset-password',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './reset-password.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ResetPassword {
  private readonly api = inject(DeployGuardApi);

  protected readonly token = tokenFromFragment(inject(ActivatedRoute).snapshot.fragment);
  protected readonly submitting = signal(false);
  protected readonly done = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string[]>>({});

  protected readonly form = inject(NonNullableFormBuilder).group(
    {
      newPassword: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
      confirmPassword: ['', Validators.required],
    },
    { validators: sameNewPassword },
  );

  constructor() {
    inject(Location).replaceState('/reset-password');
  }

  protected submit(): void {
    if (!this.token) {
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.submitting.set(true);
    this.error.set(null);
    this.serverErrors.set({});

    this.api.resetPassword(this.token, this.form.getRawValue().newPassword).subscribe({
      next: () => {
        this.submitting.set(false);
        this.done.set(true);
      },
      error: (err) => {
        this.submitting.set(false);
        const errors = fieldErrors(err);
        this.serverErrors.set(errors);
        if (Object.keys(errors).length === 0) {
          this.error.set(fullErrorMessage(err));
        }
      },
    });
  }

  protected showError(field: 'newPassword' | 'confirmPassword'): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }

  protected showMismatch(): boolean {
    return this.form.hasError('mismatch') && this.form.controls.confirmPassword.touched;
  }
}
