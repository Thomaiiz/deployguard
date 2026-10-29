import { DurationPipe } from './duration.pipe';

describe('DurationPipe', () => {
  const pipe = new DurationPipe();

  it('formats seconds, minutes and hours', () => {
    expect(pipe.transform(42)).toBe('42s');
    expect(pipe.transform(75)).toBe('1min 15s');
    expect(pipe.transform(7300)).toBe('2h 1min');
    expect(pipe.transform(null)).toBe('—');
  });
});
