import { DatePipe } from '@angular/common';
import { Component } from '@angular/core';

import { DurationPipe } from '../../../shared/pipes/duration-pipe';
import { ClockWorkflow } from '../../clock/clock-workflow';

@Component({
  selector: 'app-terminal-page',
  imports: [DatePipe, DurationPipe],
  templateUrl: './terminal-page.html',
  styleUrl: './terminal-page.scss',
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
