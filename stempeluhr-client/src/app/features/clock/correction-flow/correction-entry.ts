import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/**
 * Einstieg in den Korrekturablauf: „Korrektur“ (bzw. „Korrektur nur online“)
 * und, nach über 12 h Einstempelzeit, „Vergessen auszustempeln?“. Eine eigene
 * Komponente, damit die Stile nicht in das Stilbudget von Terminal und /clock
 * zählen.
 */
@Component({
  selector: 'app-correction-entry',
  templateUrl: './correction-entry.html',
  styleUrl: './correction-entry.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '[class.layout-terminal]': "layout() === 'terminal'",
  },
})
export class CorrectionEntry {
  readonly label = input.required<string>();
  readonly variant = input<'entry' | 'hint'>('entry');
  readonly layout = input<'terminal' | 'clock'>('terminal');
  readonly disabled = input(false);
  readonly pressed = output<void>();
}
