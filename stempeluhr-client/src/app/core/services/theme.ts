import { DOCUMENT, Injectable, inject, signal } from '@angular/core';

export type ThemeName = 'light' | 'dark';

/**
 * Setzt das Farbthema der Anwendung auf `<html data-theme="...">`.
 *
 * Der Kiosk laeuft fest im dunklen Design (Issue #39); die uebrigen Seiten
 * bleiben hell. Den ersten Anstrich VOR dem Bootstrap macht das Skript in
 * `index.html` - sonst blitzt beim Laden kurz das helle Grundgeruest auf.
 * Danach fuehrt diese Klasse das Attribut (an-/abwaehlende Routen).
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly current = signal<ThemeName>('light');

  readonly theme = this.current.asReadonly();

  apply(theme: ThemeName): void {
    this.current.set(theme);
    this.document.documentElement.dataset['theme'] = theme;
  }
}
