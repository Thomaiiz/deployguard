import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DeployGuardApi } from '../core/deployguard-api';
import { ApiKey } from '../core/models';
import { errorMessage, fieldErrors } from '../core/problem-details';

@Component({
  selector: 'dg-api-keys',
  imports: [ReactiveFormsModule, DatePipe],
  templateUrl: './api-keys.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ApiKeys {
  private readonly api = inject(DeployGuardApi);

  protected readonly keys = signal<ApiKey[]>([]);
  /** A chave completa só existe aqui, logo depois de criada. Ao sair da tela, some. */
  protected readonly newKey = signal<string | null>(null);
  protected readonly copied = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly serverErrors = signal<Record<string, string[]>>({});

  protected readonly form = inject(NonNullableFormBuilder).group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
  });

  constructor() {
    this.load();
  }

  protected load(): void {
    this.api.listApiKeys().subscribe({
      next: (keys) => this.keys.set(keys),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.serverErrors.set({});
    this.error.set(null);
    this.api.createApiKey(this.form.getRawValue().name).subscribe({
      next: (created) => {
        this.newKey.set(created.key);
        this.copied.set(false);
        this.form.reset();
        this.load();
      },
      error: (err) => {
        const errors = fieldErrors(err);
        this.serverErrors.set(errors);
        if (Object.keys(errors).length === 0) {
          this.error.set(errorMessage(err));
        }
      },
    });
  }

  protected async copy(key: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(key);
      this.copied.set(true);
    } catch {
      this.error.set('Não foi possível copiar. Selecione a chave e copie manualmente.');
    }
  }

  protected revoke(key: ApiKey): void {
    if (
      !confirm(`Revogar a chave "${key.name}"? O que usa essa chave para de funcionar na hora.`)
    ) {
      return;
    }
    this.api.deleteApiKey(key.id).subscribe({
      next: () => this.keys.update((list) => list.filter((k) => k.id !== key.id)),
      error: (err) => this.error.set(errorMessage(err)),
    });
  }
}
