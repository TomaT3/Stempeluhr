import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';

import { VersionBadge } from '../../../shared/components/version-badge/version-badge';
import { HoursOverviewCard } from '../../../shared/components/hours-overview-card/hours-overview-card';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { ThemeService } from '../../../core/services/theme';
import { ClockWorkflow } from '../../clock/clock-workflow';

@Component({
  selector: 'app-terminal-page',
  imports: [DatePipe, DurationPipe, HoursOverviewCard, VersionBadge],
  templateUrl: './terminal-page.html',
  styleUrl: './terminal-page.scss',
})
export class TerminalPage extends ClockWorkflow {
  private readonly theme = inject(ThemeService);

  constructor() {
    super();
    this.clockState.setEmployeeMode(true);
    this.theme.apply('dark');
  }

  /** Verlaesst der Kiosk die Route, faellt die App auf das helle Design zurueck. */
  override ngOnDestroy(): void {
    super.ngOnDestroy();
    this.theme.apply('light');
  }

  protected override keepFocusedShellAfterReset(): boolean {
    return true;
  }
}
