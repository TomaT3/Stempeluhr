import { HttpClient, HttpHeaders } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

import { AdminEmployeeStatus, AdminSettings, KimaiActivity, KimaiProject, KimaiUser } from '../models/admin.models';

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

  private headers(adminPassword: string): HttpHeaders {
    return new HttpHeaders({ 'X-Admin-Password': adminPassword });
  }
}
