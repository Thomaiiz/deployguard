import { Pipe, PipeTransform } from '@angular/core';

/** 75 → "1min 15s"; 7300 → "2h 1min". */
@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(totalSeconds: number | null): string {
    if (totalSeconds === null || totalSeconds < 0) {
      return '—';
    }
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = Math.floor(totalSeconds % 60);
    if (hours > 0) {
      return `${hours}h ${minutes}min`;
    }
    if (minutes > 0) {
      return `${minutes}min ${seconds}s`;
    }
    return `${seconds}s`;
  }
}
