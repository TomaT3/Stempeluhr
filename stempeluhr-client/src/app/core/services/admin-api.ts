import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

import {
  AdminEmployeeStatus,
  AdminRejectedOfflineEvent,
  AdminSettings,
  AdminTerminalStatus,
  AdminTimeCorrection,
  KimaiActivity,
  KimaiProject,
  KimaiUser,
} from '../models/admin.models';
import { NfcLatestEvent } from '../models/kiosk.models';

@Injectable({
  providedIn: 'root',
})
export class AdminApi {
  private readonly http = inject(HttpClient);

  getSettings(adminPassword: string) {
    return this.http.get<AdminSettings>('/api/admin/settings', { headers: this.headers(adminPassword) });
  }

  saveSettings(adminPassword: string, payload: unknown) {
    return this.http.put<AdminSettings>('/api/admin/settings', payload, { headers: this.headers(adminPassword) });
  }

  importKimaiUsers(adminPassword: string, baseUrl: string) {
    return this.http.post<KimaiUser[]>(
      '/api/admin/kimai-users',
      { baseUrl, adminApiToken: '' },
      { headers: this.headers(adminPassword) },
    );
  }

  importKimaiActivities(adminPassword: string, baseUrl: string) {
    return this.http.post<KimaiActivity[]>(
      '/api/admin/kimai-activities',
      { baseUrl, adminApiToken: '' },
      { headers: this.headers(adminPassword) },
    );
  }

  importKimaiProjects(adminPassword: string, baseUrl: string) {
    return this.http.post<KimaiProject[]>(
      '/api/admin/kimai-projects',
      { baseUrl, adminApiToken: '' },
      { headers: this.headers(adminPassword) },
    );
  }

  getEmployeeStatuses(adminPassword: string) {
    return this.http.get<AdminEmployeeStatus[]>('/api/admin/employee-statuses', { headers: this.headers(adminPassword) });
  }

  getTerminalStatuses(adminPassword: string) {
    return this.http.get<AdminTerminalStatus[]>('/api/admin/terminal-statuses', { headers: this.headers(adminPassword) });
  }

  getRejectedOfflineEvents(adminPassword: string) {
    return this.http.get<AdminRejectedOfflineEvent[]>('/api/admin/rejected-offline-events', { headers: this.headers(adminPassword) });
  }

  resolveRejectedOfflineEvent(adminPassword: string, eventId: string) {
    return this.http.put<void>(`/api/admin/rejected-offline-events/${encodeURIComponent(eventId)}/resolved`, {},
      { headers: this.headers(adminPassword) });
  }

  /** `open`: Status pending und failed; `all`: auch die abgeschlossenen. */
  getCorrections(adminPassword: string, status: 'open' | 'all') {
    return this.http.get<AdminTimeCorrection[]>('/api/admin/corrections',
      { headers: this.headers(adminPassword), params: { status } });
  }

  approveCorrection(adminPassword: string, id: string) {
    return this.http.post<AdminTimeCorrection>(`/api/admin/corrections/${encodeURIComponent(id)}/approve`, {},
      { headers: this.headers(adminPassword) });
  }

  rejectCorrection(adminPassword: string, id: string, note: string) {
    return this.http.post<AdminTimeCorrection>(`/api/admin/corrections/${encodeURIComponent(id)}/reject`,
      { note: note || null }, { headers: this.headers(adminPassword) });
  }

  retryCorrection(adminPassword: string, id: string) {
    return this.http.post<AdminTimeCorrection>(`/api/admin/corrections/${encodeURIComponent(id)}/retry`, {},
      { headers: this.headers(adminPassword) });
  }

  resolveCorrection(adminPassword: string, id: string) {
    return this.http.put<AdminTimeCorrection>(`/api/admin/corrections/${encodeURIComponent(id)}/resolved`, {},
      { headers: this.headers(adminPassword) });
  }

  latestNfcEvent(terminalId: string, fallbackToAny = false) {
    const params: Record<string, string> = { terminalId };
    if (fallbackToAny) {
      params['fallbackToAny'] = 'true';
    }

    return this.http.get<NfcLatestEvent>('/api/nfc/events/latest', { params });
  }

  private headers(adminPassword: string): HttpHeaders {
    return new HttpHeaders({ 'X-Admin-Password': adminPassword });
  }
}
