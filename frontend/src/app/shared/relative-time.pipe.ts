import { Pipe, PipeTransform } from '@angular/core';

/** "há 12 s", "há 3 min"... O parâmetro `now` força o recálculo a cada atualização. */
@Pipe({ name: 'relativeTime' })
export class RelativeTimePipe implements PipeTransform {
  transform(value: string | null, now: number = Date.now()): string {
    if (!value) {
      return 'nunca';
    }
    const seconds = Math.max(0, Math.round((now - new Date(value).getTime()) / 1000));
    if (seconds < 60) {
      return `há ${seconds} s`;
    }
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) {
      return `há ${minutes} min`;
    }
    const hours = Math.round(minutes / 60);
    if (hours < 48) {
      return `há ${hours} h`;
    }
    return `há ${Math.round(hours / 24)} dias`;
  }
}
