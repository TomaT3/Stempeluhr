import { computed, Injectable, signal } from '@angular/core';

/**
 * Merkt sich das bestätigte Admin-Passwort für alle Admin-Seiten.
 *
 * sessionStorage statt localStorage: Die Anmeldung übersteht Seitenwechsel
 * und Neuladen im selben Tab, endet aber mit dem Schließen von Tab/Browser.
 * Ist der Speicher gesperrt (privater Modus), gilt sie nur bis zum Neuladen.
 */
@Injectable({ providedIn: 'root' })
export class AdminSession {
  static readonly StorageKey = 'stempeluhr.admin.session';

  private readonly current = signal(AdminSession.read());

  readonly password = this.current.asReadonly();
  readonly isLoggedIn = computed(() => this.current() !== '');

  remember(password: string): void {
    this.current.set(password);
    try {
      sessionStorage.setItem(AdminSession.StorageKey, password);
    } catch {
      // Nur im Speicher behalten.
    }
  }

  clear(): void {
    this.current.set('');
    try {
      sessionStorage.removeItem(AdminSession.StorageKey);
    } catch {
      // Nichts gespeichert, das entfernt werden müsste.
    }
  }

  private static read(): string {
    try {
      return sessionStorage.getItem(AdminSession.StorageKey) ?? '';
    } catch {
      return '';
    }
  }
}
