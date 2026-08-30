import { Component, OnInit, computed, inject, signal } from '@angular/core';

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
  private readonly kioskApi = inject(KioskApi);

  readonly clientVersion = APP_VERSION;
  readonly serverVersion = signal<string | null>(null);

  /** True sobald der Server eine Version meldet, die von der Client-Version abweicht. */
  readonly versionMismatch = computed(
    () => this.serverVersion() !== null && this.serverVersion() !== APP_VERSION,
  );

  ngOnInit(): void {
    this.kioskApi.health().subscribe({
      next: health => this.serverVersion.set(health.version),
      // Server nicht erreichbar: Badge zeigt weiterhin nur die Client-Version.
      error: () => this.serverVersion.set(null),
    });
  }
}
