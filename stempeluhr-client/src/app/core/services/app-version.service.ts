import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BehaviorSubject, catchError, distinctUntilChanged, interval, of, startWith, switchMap, timeout } from 'rxjs';

import { APP_VERSION } from '../app-version';
import { KioskApi } from './kiosk-api';

/**
 * Pollt die Server-Version (GET /api/health) sofort + alle 60 s und stellt
 * sie als Signal (Version-Badge) und als Observable (Auto-Reload) bereit —
 * ein Poll, mehrere Konsumenten.
 */
@Injectable({ providedIn: 'root' })
export class AppVersionService {
  /** Poll-Intervall: Server-Version live halten (Kiosk läuft tagelang). */
  private static readonly RefreshIntervalMs = 60_000;

  private readonly destroyRef = inject(DestroyRef);
  private readonly kioskApi = inject(KioskApi);

  private readonly versionSubject = new BehaviorSubject<string | null>(null);

  /** Zuletzt bekannte Server-Version (null = noch nie erfolgreich gepollt). */
  readonly serverVersion = signal<string | null>(null);

  /** True sobald der Server eine Version meldet, die von der Client-Version abweicht. */
  readonly versionMismatch = computed(
    () => this.serverVersion() !== null && this.serverVersion() !== APP_VERSION,
  );

  /** Feuert NUR bei ÄNDERUNG der Server-Version (distinctUntilChanged). */
  readonly version$ = this.versionSubject.pipe(distinctUntilChanged());

  constructor() {
    // startWith(0): erster Poll SYNCHRON beim Subscribe (timer(0, …) wäre
    // async und erschwert Tests); danach alle 60 s.
    interval(AppVersionService.RefreshIntervalMs)
      .pipe(
        startWith(0),
        // Kein Default-Timeout im HttpClient: bei einem Server, der TCP
        // akzeptiert aber nie antwortet, schlägt der Poll nach 10 s
        // deterministisch fehl (letzte bekannte Version bleibt stehen).
        switchMap(() =>
          this.kioskApi.health().pipe(timeout(10_000), catchError(() => of(null))),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe(health => {
        if (health) {
          this.serverVersion.set(health.version);
          this.versionSubject.next(health.version);
        }
      });
  }
}
