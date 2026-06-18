import { Routes } from '@angular/router';

import { AdminPage } from './features/admin/admin-page/admin-page';
import { ClockPage } from './features/clock/clock-page/clock-page';

export const routes: Routes = [
  { path: '', redirectTo: 'clock', pathMatch: 'full' },
  { path: 'clock', component: ClockPage, title: 'Stempeluhr' },
  { path: 'admin', component: AdminPage, title: 'Stempeluhr Admin' },
  { path: '**', redirectTo: 'clock' },
];
