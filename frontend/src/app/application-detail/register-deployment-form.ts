import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DeployGuardApi } from '../core/deployguard-api';
import { errorMessage, fieldErrors } from '../core/problem-details';

/** Registro manual de deploy (o normal é o pipeline de CI registrar com uma chave de API). */
@Component({
  selector: 'dg-register-deployment-form',
  imports: [ReactiveFormsModule],
  templateUrl: './register-deployment-form.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RegisterDeploymentForm {
  private readonly api = inject(DeployGuardApi);

  readonly applicationId = input.required<string>();
  readonly registered = output<void>();

  protected readonly form = inject(NonNullableFormBuilder).group({
    version: ['', [Validators.required, Validators.maxLength(100)]],
    commitSha: ['', Validators.pattern(/^[0-9a-fA-F]{7,40}$/)],
    description: ['', Validators.maxLength(500)],
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

    const { version, commitSha, description } = this.form.getRawValue();
    this.api
      .createDeployment(this.applicationId(), {
        version,
        commitSha: commitSha || null,
        description: description || null,
      })
      .subscribe({
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
}
