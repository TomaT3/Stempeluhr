import { ChangeDetectionStrategy, Component, computed, input, model, signal } from '@angular/core';

type Layer = 'letters' | 'symbols';

/** QWERTZ ohne Umlaute: WPA-Passwörter erlauben nur druckbare ASCII-Zeichen. */
const LETTERS = ['1234567890', 'qwertzuiop', 'asdfghjkl', 'yxcvbnm'];
const SYMBOLS = ['1234567890', '!@#$%&*()-', '_+=/\\|~^\'"', '[]{}<>:;,.?`'];

/**
 * Bildschirmtastatur für den Touch-Kiosk: Chromium im Wayland-Kiosk bringt
 * keine System-Tastatur mit. Schreibt direkt in `value`.
 */
@Component({
  selector: 'app-onscreen-keyboard',
  templateUrl: './onscreen-keyboard.html',
  styleUrl: './onscreen-keyboard.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OnscreenKeyboard {
  readonly value = model('');
  readonly maxLength = input(64);

  readonly layer = signal<Layer>('letters');
  readonly shift = signal(false);

  readonly rows = computed(() => {
    const rows = this.layer() === 'letters' ? LETTERS : SYMBOLS;
    const upper = this.layer() === 'letters' && this.shift();
    return rows.map(row => [...(upper ? row.toUpperCase() : row)]);
  });

  press(key: string): void {
    if (this.value().length >= this.maxLength()) return;
    this.value.update(value => value + key);
    // Wie am Handy: Umschalt gilt für einen Buchstaben.
    this.shift.set(false);
  }

  backspace(): void {
    this.value.update(value => value.slice(0, -1));
  }

  toggleShift(): void {
    this.shift.update(shift => !shift);
  }

  toggleLayer(): void {
    this.layer.update(layer => (layer === 'letters' ? 'symbols' : 'letters'));
    this.shift.set(false);
  }

  keyLabel(key: string): string {
    return key === ' ' ? 'Leerzeichen' : key;
  }
}
