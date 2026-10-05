import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';

import { addMinutes, formatDay, formatTime, minutesBetween } from './local-time';

/**
 * Zeit einstellen ohne Tastatur (Kiosk): Tag ±1, Stunde ±1, Minute ±5 und ±1
 * mit großen Touch-Flächen. Der Wert ist lokale Zeit `yyyy-MM-ddTHH:mm` und
 * wird als Wanduhr gerechnet (siehe local-time.ts); ein Schritt über Stunde
 * oder Tag hinaus rollt weiter. `min`/`max` (inklusive) sperren die Knöpfe,
 * die darüber hinaus führen würden - die API prüft trotzdem noch einmal.
 * Startet der Wert außerhalb (z. B. ein Eintrag über 16 h, der gekürzt werden
 * soll), bleiben die Schritte erlaubt, die ihn dem Bereich näher bringen;
 * nur Schritte, die sich weiter davon entfernen, sind gesperrt.
 */
@Component({
  selector: 'app-time-stepper',
  templateUrl: './time-stepper.html',
  styleUrl: './time-stepper.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimeStepper {
  /** Zugänglicher Name der Gruppe, z. B. „Pause beginnt“. */
  readonly label = input('Zeit');
  readonly value = model.required<string>();
  readonly min = input<string | null>(null);
  readonly max = input<string | null>(null);
  readonly disabled = input(false);

  protected readonly day = computed(() => formatDay(this.value()));
  protected readonly hour = computed(() => formatTime(this.value()).slice(0, 2));
  protected readonly minute = computed(() => formatTime(this.value()).slice(3, 5));

  /** Führt der Schritt in den erlaubten Bereich - oder, von außerhalb, näher an ihn heran? */
  protected canStep(minutes: number): boolean {
    if (this.disabled()) {
      return false;
    }
    const next = this.distance(addMinutes(this.value(), minutes));
    return next === 0 || next < this.distance(this.value());
  }

  /** Minuten bis zum erlaubten Bereich; 0 innerhalb. */
  private distance(value: string): number {
    const min = this.min();
    const max = this.max();
    if (min !== null && value < min) {
      return minutesBetween(value, min);
    }
    return max !== null && value > max ? minutesBetween(max, value) : 0;
  }

  protected step(minutes: number): void {
    if (this.canStep(minutes)) {
      this.value.set(addMinutes(this.value(), minutes));
    }
  }
}
