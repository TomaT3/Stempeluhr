import { DatePipe } from '@angular/common';
import { Component } from '@angular/core';

import { VersionBadge } from '../../../shared/components/version-badge/version-badge';
import { HoursOverviewCard } from '../../../shared/components/hours-overview-card/hours-overview-card';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { ClockWorkflow } from '../../clock/clock-workflow';
import { CorrectionEntry } from '../../clock/correction-flow/correction-entry';
import { CorrectionFlow } from '../../clock/correction-flow/correction-flow';

@Component({
  selector: 'app-terminal-page',
  imports: [CorrectionEntry, CorrectionFlow, DatePipe, DurationPipe, HoursOverviewCard, VersionBadge],
  templateUrl: './terminal-page.html',
  styleUrl: './terminal-page.scss',
  host: {
    // Touch-Kiosk: langes Tippen öffnet kein Kontextmenü (siehe :host-Styles).
    '(contextmenu)': '$event.preventDefault()',
  },
})
export class TerminalPage extends ClockWorkflow {
  constructor() {
    super();
    this.clockState.setEmployeeMode(true);
  }

  protected override keepFocusedShellAfterReset(): boolean {
    return true;
  }
}
