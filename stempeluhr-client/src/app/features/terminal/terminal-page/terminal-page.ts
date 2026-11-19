import { DatePipe } from '@angular/common';
import { Component, signal } from '@angular/core';

import { VersionBadge } from '../../../shared/components/version-badge/version-badge';
import { HoursOverviewCard } from '../../../shared/components/hours-overview-card/hours-overview-card';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { AbsenceFlow } from '../../clock/absence-flow/absence-flow';
import { ClockWorkflow } from '../../clock/clock-workflow';
import { CorrectionEntry } from '../../clock/correction-flow/correction-entry';
import { CorrectionFlow } from '../../clock/correction-flow/correction-flow';
import { TerminalMaintenance } from '../terminal-maintenance/terminal-maintenance';

/** Fünfmal auf die Uhrzeit tippen, innerhalb dieser Zeit, öffnet die WLAN-Einstellung. */
export const MAINTENANCE_TAPS = 5;
export const MAINTENANCE_TAP_WINDOW_MS = 3_000;

@Component({
  selector: 'app-terminal-page',
  imports: [AbsenceFlow, CorrectionEntry, CorrectionFlow, DatePipe, DurationPipe, HoursOverviewCard, TerminalMaintenance,
    VersionBadge],
  templateUrl: './terminal-page.html',
  styleUrl: './terminal-page.scss',
  host: {
    // Touch-Kiosk: langes Tippen öffnet kein Kontextmenü (siehe :host-Styles).
    '(contextmenu)': '$event.preventDefault()',
  },
})
export class TerminalPage extends ClockWorkflow {
  readonly maintenanceOpen = signal(false);
  private maintenanceTaps: number[] = [];

  constructor() {
    super();
    this.clockState.setEmployeeMode(true);
  }

  /**
   * Versteckter Zugang zur WLAN-Einstellung, nur ohne angemeldeten
   * Mitarbeiter. Die Wartungs-PIN schützt den Rest.
   */
  maintenanceTap(): void {
    if (this.selectedEmployee()) return;
    const now = Date.now();
    this.maintenanceTaps = [...this.maintenanceTaps.filter(at => now - at < MAINTENANCE_TAP_WINDOW_MS), now];
    if (this.maintenanceTaps.length >= MAINTENANCE_TAPS) {
      this.maintenanceTaps = [];
      this.maintenanceOpen.set(true);
    }
  }

  protected override keepFocusedShellAfterReset(): boolean {
    return true;
  }
}
