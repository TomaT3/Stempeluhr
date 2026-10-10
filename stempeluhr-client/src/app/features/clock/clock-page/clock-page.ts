import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { Component } from '@angular/core';

import { Avatar } from '../../../shared/components/avatar/avatar';
import { HoursOverviewCard } from '../../../shared/components/hours-overview-card/hours-overview-card';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';
import { VersionBadge } from '../../../shared/components/version-badge/version-badge';
import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { ClockWorkflow } from '../clock-workflow';
import { CorrectionEntry } from '../correction-flow/correction-entry';
import { CorrectionFlow } from '../correction-flow/correction-flow';

@Component({
  selector: 'app-clock-page',
  imports: [Avatar, CorrectionEntry, CorrectionFlow, DatePipe, DurationPipe, HoursOverviewCard, NgTemplateOutlet, StatusBadge, VersionBadge],
  templateUrl: './clock-page.html',
  styleUrl: './clock-page.scss',
})
export class ClockPage extends ClockWorkflow {}
