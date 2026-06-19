import { DatePipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { ClockState } from './core/services/clock-state';

@Component({
  selector: 'app-root',
  imports: [DatePipe, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  readonly clockState = inject(ClockState);
}
