import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { User, UserRole } from '../core/models';
import { errorMessage, fieldErrors } from '../core/problem-details';

@Component({
  selector: 'dg-users',
  imports: [ReactiveFormsModule, DatePipe],
  templateUrl: './users.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Users {
  private readonly api = inject(DeployGuardApi);
  protected readonly auth = inject(AuthService);

  protected readonly users = signal<User[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string[]>>({});
  protected readonly submitting = signal(false);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.email]],
    // Mesma regra do backend (12+ caracteres); a validação que vale é a da API.
    password: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
    role: ['Viewer' as UserRole, Validators.required],
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.api.listUsers().subscribe({
      next: (users) => this.users.set(users),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.submitting.set(true);
    this.serverErrors.set({});
    this.error.set(null);

    this.api.createUser(this.form.getRawValue()).subscribe({
      next: () => {
        this.submitting.set(false);
        this.form.reset();
        this.load();
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

  protected remove(user: User): void {
    if (!confirm(`Remover o acesso de ${user.email}?`)) {
      return;
    }
    this.api.deleteUser(user.id).subscribe({
      next: () => this.users.update((list) => list.filter((u) => u.id !== user.id)),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected showError(field: 'email' | 'password'): boolean {
    const control = this.form.controls[field];
    return control.invalid && control.touched;
  }
}
