import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { HoursOverview } from '../../../core/models/kiosk.models';
import { DurationPipe } from '../../pipes/duration-pipe';

/**
 * Stundenübersicht-Karte (Heute / Woche / Monat). Rendert nichts, solange
 * keine Daten vorliegen. Wird auf der Clock-Seite (Desktop/Handy) und auf
 * dem Terminal-Kiosk (7"-Display) verwendet; der Kiosk bekommt über
 * `:host-context(.terminal)` die kompakte Variante.
 */
@Component({
  selector: 'app-hours-overview-card',
  imports: [DurationPipe],
  templateUrl: './hours-overview-card.html',
  styleUrl: './hours-overview-card.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HoursOverviewCard {
  readonly hours = input<HoursOverview | null>(null);
}
