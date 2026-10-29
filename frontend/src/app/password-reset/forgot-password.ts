import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { DeployGuardApi } from '../core/deployguard-api';
import { errorMessage } from '../core/problem-details';

@Component({
  selector: 'dg-forgot-password',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './forgot-password.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ForgotPassword {
  private readonly api = inject(DeployGuardApi);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(254)]],
  });

  protected readonly submitting = signal(false);
  /** A mensagem da API é a mesma para qualquer e-mail: a tela não revela se a conta existe. */
  protected readonly sent = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.submitting.set(true);
    this.error.set(null);

    this.api.forgotPassword(this.form.getRawValue().email).subscribe({
      next: (response) => {
        this.submitting.set(false);
        this.sent.set(response.message);
      },
      error: (err) => {
        this.submitting.set(false);
        this.error.set(errorMessage(err));
      },
    });
  }
}
