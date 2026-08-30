import { DatePipe } from '@angular/common';
import { Component } from '@angular/core';

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
export class ClockPage extends ClockWorkflow {}
