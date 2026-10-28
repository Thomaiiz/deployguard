import { TestBed } from '@angular/core/testing';
import { StatusBadge } from './status-badge';

describe('StatusBadge', () => {
  function render(status: 'Up' | 'Degraded' | 'Down' | null, enabled = true): HTMLElement {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('enabled', enabled);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('.badge');
  }

  it('shows a label and modifier class per status', () => {
    expect(render('Up').textContent).toContain('Operacional');
    expect(render('Down').className).toContain('badge--down');
    expect(render('Degraded').textContent).toContain('Degradado');
  });

  it('shows pending before the first check and paused when disabled', () => {
    expect(render(null).textContent).toContain('Aguardando');
    expect(render('Up', false).textContent).toContain('Pausado');
  });
});
