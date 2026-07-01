import { Routes } from '@angular/router';

import { AdminPage } from './features/admin/admin-page/admin-page';
import { EmployeeStatusPage } from './features/admin/employee-status-page/employee-status-page';
import { ClockPage } from './features/clock/clock-page/clock-page';
import { TerminalPage } from './features/terminal/terminal-page/terminal-page';

export const routes: Routes = [
  { path: '', redirectTo: 'clock', pathMatch: 'full' },
  { path: 'clock', component: ClockPage, title: 'Stempeluhr' },
  { path: 'terminal', component: TerminalPage, title: 'Stempeluhr Terminal' },
  { path: 'admin', component: AdminPage, title: 'Stempeluhr Admin' },
  { path: 'admin/status', component: EmployeeStatusPage, title: 'Mitarbeiterstatus' },
  { path: '**', redirectTo: 'clock' },
];
