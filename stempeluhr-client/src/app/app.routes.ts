import { Routes } from '@angular/router';

import { AdminPage } from './features/admin/admin-page/admin-page';
import { CorrectionsPage } from './features/admin/corrections-page/corrections-page';
import { EmployeeStatusPage } from './features/admin/employee-status-page/employee-status-page';
import { RejectedOfflinePage } from './features/admin/rejected-offline-page/rejected-offline-page';
import { TerminalStatusPage } from './features/admin/terminal-status-page/terminal-status-page';
import { ClockPage } from './features/clock/clock-page/clock-page';
import { TerminalPage } from './features/terminal/terminal-page/terminal-page';

export const routes: Routes = [
  { path: '', redirectTo: 'clock', pathMatch: 'full' },
  { path: 'clock', component: ClockPage, title: 'Stempeluhr' },
  { path: 'terminal', component: TerminalPage, title: 'Stempeluhr Terminal' },
  { path: 'admin', component: AdminPage, title: 'Stempeluhr Admin' },
  { path: 'admin/status', component: EmployeeStatusPage, title: 'Mitarbeiterstatus' },
  { path: 'admin/terminals', component: TerminalStatusPage, title: 'Terminalstatus' },
  { path: 'admin/offline-rejections', component: RejectedOfflinePage, title: 'Abgelehnte Offline-Stempel' },
  { path: 'admin/corrections', component: CorrectionsPage, title: 'Korrekturanträge' },
  { path: '**', redirectTo: 'clock' },
];
