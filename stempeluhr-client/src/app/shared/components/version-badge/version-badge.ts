import { Component, inject } from '@angular/core';

import { APP_VERSION } from '../../../core/app-version';
import { AppVersionService } from '../../../core/services/app-version.service';

/**
 * Zeigt die geladene Client-Version und die Server-Version der API als
 * kleines, nicht-interaktives Badge (fixed unten rechts).
 *
 * Bei Versions-Abweichung (z.B. alte App aus dem Browser-Cache nach einem
 * Update) bekommt das Badge eine Warnfarbe — so ist sofort erkennbar, dass
 * der Client neu geladen werden muss.
 *
 * Die Server-Version kommt aus dem gemeinsamen `AppVersionService`
 * (ein health()-Poll alle 60 s für Badge UND Auto-Reload).
 */
@Component({
  selector: 'app-version-badge',
  imports: [],
  templateUrl: './version-badge.html',
  styleUrl: './version-badge.scss',
})
export class VersionBadge {
  private readonly appVersion = inject(AppVersionService);

  readonly clientVersion = APP_VERSION;
  readonly serverVersion = this.appVersion.serverVersion;
  readonly versionMismatch = this.appVersion.versionMismatch;
}
