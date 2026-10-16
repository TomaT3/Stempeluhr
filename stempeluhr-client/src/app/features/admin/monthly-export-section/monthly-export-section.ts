import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';

/**
 * Abschnitt „Monatsexport“ der Admin-Seite: lädt die Excel-Datei für den
 * Steuerberater. Die Datei braucht den Header X-Admin-Password, deshalb kommt
 * sie über HttpClient als Blob statt über einen Link. Eigene Komponente, damit
 * sie lazy nachgeladen wird und das Start-Bundle der Admin-Seite klein bleibt.
 */
@Component({
  selector: 'app-monthly-export-section',
  templateUrl: './monthly-export-section.html',
  styleUrl: './monthly-export-section.scss',
})
export class MonthlyExportSection {
  private readonly adminApi = inject(AdminApi);
  private readonly adminSession = inject(AdminSession);
  private readonly destroyRef = inject(DestroyRef);

  /** Gewählter Monat (<c>yyyy-MM</c>), vorbelegt mit dem Vormonat. */
  readonly month = signal(MonthlyExportSection.previousMonth(new Date()));
  readonly isBusy = signal(false);
  readonly message = signal('');

  /** Das Backend hat das Admin-Passwort abgelehnt; die Sitzung ist bereits beendet. */
  readonly sessionRejected = output<void>();

  download(): void {
    if (this.isBusy()) {
      return;
    }

    const match = /^(\d{4})-(\d{2})$/.exec(this.month());
    const year = match ? Number(match[1]) : 0;
    const monthNumber = match ? Number(match[2]) : 0;
    if (!match || year < 2000 || monthNumber < 1 || monthNumber > 12) {
      this.message.set('Bitte einen Monat wählen.');
      return;
    }

    const password = this.adminSession.password();
    this.message.set('');
    this.isBusy.set(true);
    this.adminApi.downloadMonthlyExport(password, year, monthNumber).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: file => {
        this.saveFile(file, `Stempeluhr-Export-${match[1]}-${match[2]}.xlsx`);
        this.isBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401 && this.adminSession.password() === password) {
          this.adminSession.clear();
          this.sessionRejected.emit();
        }
        this.message.set(MonthlyExportSection.errorMessage(error));
        this.isBusy.set(false);
      },
    });
  }

  /** Startet den Browser-Download der Datei. */
  private saveFile(file: Blob, fileName: string): void {
    const url = URL.createObjectURL(file);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  private static errorMessage(error: HttpErrorResponse): string {
    switch (error.status) {
      case 0:
        return 'Backend nicht erreichbar. Der Export wurde nicht erstellt.';
      case 400:
        return 'Ungültiger Monat.';
      case 401:
        return 'Admin-Passwort ungültig. Bitte neu anmelden.';
      default:
        return `Der Export konnte nicht erstellt werden (${error.status}).`;
    }
  }

  /** Vormonat von <c>today</c> als <c>yyyy-MM</c>. */
  private static previousMonth(today: Date): string {
    const previous = new Date(today.getFullYear(), today.getMonth() - 1, 1);
    return `${previous.getFullYear()}-${String(previous.getMonth() + 1).padStart(2, '0')}`;
  }
}
