import { ChangeDetectionStrategy, Component, inject, output, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DeployGuardApi } from '../core/deployguard-api';
import { errorMessage, fieldErrors } from '../core/problem-details';

@Component({
  selector: 'dg-register-application-form',
  imports: [ReactiveFormsModule],
  templateUrl: './register-application-form.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RegisterApplicationForm {
  private readonly api = inject(DeployGuardApi);

  readonly registered = output<void>();

  // Validação no cliente melhora a experiência, mas a regra de verdade (allowlist,
  // bloqueio de IP privado) fica no backend: o cliente nunca é confiável.
  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    healthCheckUrl: [
      '',
      [Validators.required, Validators.maxLength(2048), Validators.pattern(/^https?:\/\/.+/i)],
    ],
    checkIntervalSeconds: [60, [Validators.required, Validators.min(10)]],
  });

  protected readonly submitting = signal(false);
  protected readonly serverErrors = signal<Record<string, string[]>>({});
  protected readonly generalError = signal<string | null>(null);

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.submitting.set(true);
    this.serverErrors.set({});
    this.generalError.set(null);

    this.api.createApplication(this.form.getRawValue()).subscribe({
      next: () => {
        this.submitting.set(false);
        this.form.reset();
        this.registered.emit();
      },
      error: (error) => {
        this.submitting.set(false);
        const errors = fieldErrors(error);
        this.serverErrors.set(errors);
        if (Object.keys(errors).length === 0) {
          this.generalError.set(errorMessage(error));
        }
      },
    });
  }

  protected showError(field: 'name' | 'healthCheckUrl' | 'checkIntervalSeconds'): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }
}
