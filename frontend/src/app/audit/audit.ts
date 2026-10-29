import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { DeployGuardApi } from '../core/deployguard-api';
import { AuditAction, AuditEvent } from '../core/models';
import { errorMessage } from '../core/problem-details';
import { AUDIT_LABELS, AUDIT_WARNINGS } from '../shared/audit-labels';

const PAGE_SIZE = 50;

@Component({
  selector: 'dg-audit',
  imports: [DatePipe],
  templateUrl: './audit.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class Audit {
  private readonly api = inject(DeployGuardApi);

  protected readonly labels = AUDIT_LABELS;
  protected readonly warnings = AUDIT_WARNINGS;
  protected readonly actions = Object.keys(AUDIT_LABELS) as AuditAction[];

  protected readonly events = signal<AuditEvent[]>([]);
  protected readonly nextBefore = signal<number | null>(null);
  protected readonly filter = signal<AuditAction | ''>('');
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected onFilter(value: string): void {
    this.filter.set(value as AuditAction | '');
    this.load();
  }

  protected load(): void {
    this.fetch(undefined, (page) => this.events.set(page));
  }

  protected loadMore(): void {
    const before = this.nextBefore();
    if (before !== null) {
      this.fetch(before, (page) => this.events.update((current) => [...current, ...page]));
    }
  }

  private fetch(before: number | undefined, apply: (items: AuditEvent[]) => void): void {
    this.api.listAudit({ limit: PAGE_SIZE, before, action: this.filter() || undefined }).subscribe({
      next: (page) => {
        apply(page.items);
        this.nextBefore.set(page.nextBefore);
        this.error.set(null);
      },
      error: (err) => this.error.set(errorMessage(err)),
    });
  }
}
