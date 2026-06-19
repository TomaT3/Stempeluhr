import { Component, input } from '@angular/core';

@Component({
  selector: 'app-status-badge',
  imports: [],
  templateUrl: './status-badge.html',
  styleUrl: './status-badge.scss',
})
export class StatusBadge {
  readonly text = input('');
  readonly running = input(false);
  readonly paused = input(false);
  readonly available = input(true);
  readonly compact = input(false);
}
