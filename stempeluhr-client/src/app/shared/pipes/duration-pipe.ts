import { Pipe, PipeTransform } from '@angular/core';

@Pipe({
  name: 'duration',
})
export class DurationPipe implements PipeTransform {
  transform(value: number | null | undefined, includeSeconds = true): string {
    const totalSeconds = Math.max(0, Math.floor(value ?? 0));
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    if (!includeSeconds) {
      // Kompakte Anzeige (Stundenübersicht): nur HH:mm
      return [hours, minutes]
        .map(part => part.toString().padStart(2, '0'))
        .join(':');
    }

    return [hours, minutes, seconds]
      .map(part => part.toString().padStart(2, '0'))
      .join(':');
  }
}
