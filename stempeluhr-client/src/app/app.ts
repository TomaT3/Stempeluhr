import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { ClockState } from './core/services/clock-state';
import { DurationPipe } from './shared/pipes/duration-pipe';

@Component({
  selector: 'app-root',
  imports: [DurationPipe, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  readonly clockState = inject(ClockState);
}
