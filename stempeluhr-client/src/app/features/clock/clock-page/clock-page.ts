import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';

import { ThemeService } from '../../../core/services/theme';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { HoursOverviewCard } from '../../../shared/components/hours-overview-card/hours-overview-card';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';
import { VersionBadge } from '../../../shared/components/version-badge/version-badge';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { ClockWorkflow } from '../clock-workflow';

@Component({
  selector: 'app-clock-page',
  imports: [Avatar, DatePipe, DurationPipe, HoursOverviewCard, StatusBadge, VersionBadge],
  templateUrl: './clock-page.html',
  styleUrl: './clock-page.scss',
})
export class ClockPage extends ClockWorkflow {
  private readonly theme = inject(ThemeService);

  constructor() {
    super();
    /*
     * Der Kiosk faerbt das Dokument dunkel (TerminalPage), das erste Anstrich-
     * Skript in index.html greift schon vorher. Diese Seite ist ausdruecklich
     * hell und sagt das selbst: endet der Pfad auf /terminal, loest der Router
     * aber diese Seite auf (z. B. /foo/terminal -> Wildcard -> /clock), wird
     * TerminalPage nie erzeugt - ohne diese Zeile bliebe das Dokument dunkel.
     */
    this.theme.apply('light');
  }
}
