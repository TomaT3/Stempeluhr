import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, interval, of, startWith, switchMap, timeout } from 'rxjs';

import { APP_VERSION } from '../../../core/app-version';
import { KioskApi } from '../../../core/services/kiosk-api';

/**
 * Zeigt die geladene Client-Version und die Server-Version der API als
 * kleines, nicht-interaktives Badge (fixed unten rechts).
 *
 * Bei Versions-Abweichung (z.B. alte App aus dem Browser-Cache nach einem
 * Update) bekommt das Badge eine Warnfarbe — so ist sofort erkennbar, dass
 * der Client neu geladen werden muss.
 */
@Component({
  selector: 'app-version-badge',
  imports: [],
  templateUrl: './version-badge.html',
  styleUrl: './version-badge.scss',
})
export class VersionBadge implements OnInit {
  /** Poll-Intervall: Server-Version live halten (Kiosk läuft tagelang). */
  private static readonly RefreshIntervalMs = 60_000;

  private readonly destroyRef = inject(DestroyRef);
  private readonly kioskApi = inject(KioskApi);

  readonly clientVersion = APP_VERSION;
  readonly serverVersion = signal<string | null>(null);

  /** True sobald der Server eine Version meldet, die von der Client-Version abweicht. */
  readonly versionMismatch = computed(
    () => this.serverVersion() !== null && this.serverVersion() !== APP_VERSION,
  );

  ngOnInit(): void {
    // Sofort + alle 60 s: Server-Version aktuell halten, auch wenn der Server
    // während der Kiosk-Laufzeit aktualisiert wird. Fehler (Server down)
    // lassen die zuletzt bekannte Version stehen; ohne je eine gesehene
    // Version bleibt der Platzhalter '–'.
    interval(VersionBadge.RefreshIntervalMs)
      .pipe(
        startWith(0),
        switchMap(() =>
          this.kioskApi
            .health()
            // Kein Default-Timeout im HttpClient: bei einem Server, der TCP
            // akzeptiert aber nie antwortet, schlägt der Poll nach 10 s
            // deterministisch fehl (letzte bekannte Version bleibt stehen),
            // statt bis zum nächsten Tick offen zu bleiben.
            .pipe(timeout(10_000), catchError(() => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(health => {
        if (health) {
          this.serverVersion.set(health.version);
        }
      });
  }
}
