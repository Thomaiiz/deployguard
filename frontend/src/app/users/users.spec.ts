import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../core/auth';
import { DeployGuardApi } from '../core/deployguard-api';
import { User } from '../core/models';
import { Users } from './users';

const locked: User = {
  id: '2',
  email: 'travado@example.com',
  role: 'Viewer',
  createdAt: new Date().toISOString(),
  lastLoginAt: null,
  lockedUntil: new Date(Date.now() + 10 * 60_000).toISOString(),
};

describe('Users', () => {
  it('shows locked accounts and unlocks them', async () => {
    const unlockUser = vi.fn(() => of(undefined));
    TestBed.configureTestingModule({
      imports: [Users],
      providers: [
        { provide: DeployGuardApi, useValue: { listUsers: () => of([locked]), unlockUser } },
        {
          provide: AuthService,
          useValue: { user: signal({ id: '1', email: 'admin@example.com', role: 'Admin' }) },
        },
      ],
    });
    const fixture = TestBed.createComponent(Users);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('[data-testid=locked]')?.textContent).toContain('Bloqueado até');

    const unlock = [...element.querySelectorAll('button')].find((b) =>
      b.textContent?.includes('Desbloquear'),
    )!;
    unlock.click();
    await fixture.whenStable();

    expect(unlockUser).toHaveBeenCalledWith('2');
    expect(element.querySelector('[data-testid=locked]')).toBeNull();
  });
});
