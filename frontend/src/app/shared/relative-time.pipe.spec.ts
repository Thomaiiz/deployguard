import { RelativeTimePipe } from './relative-time.pipe';

describe('RelativeTimePipe', () => {
  const pipe = new RelativeTimePipe();
  const now = new Date('2026-10-28T12:00:00Z').getTime();

  it('formats seconds, minutes, hours and days', () => {
    expect(pipe.transform('2026-10-28T11:59:48Z', now)).toBe('há 12 s');
    expect(pipe.transform('2026-10-28T11:55:00Z', now)).toBe('há 5 min');
    expect(pipe.transform('2026-10-28T09:00:00Z', now)).toBe('há 3 h');
    expect(pipe.transform('2026-10-25T12:00:00Z', now)).toBe('há 3 dias');
  });

  it('handles missing values', () => {
    expect(pipe.transform(null, now)).toBe('nunca');
  });
});
