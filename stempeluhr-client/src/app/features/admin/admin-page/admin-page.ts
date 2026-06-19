import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminEmployee, AdminEmployeeStatus, AdminSettings, KimaiUser } from '../../../core/models/admin.models';
import { AdminApi } from '../../../core/services/admin-api';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';

@Component({
  selector: 'app-admin-page',
  imports: [Avatar, RouterLink, StatusBadge],
  templateUrl: './admin-page.html',
  styleUrl: './admin-page.scss',
})
export class AdminPage {
  private readonly adminApi = inject(AdminApi);

  readonly adminPassword = signal('');
  readonly adminSettings = signal<AdminSettings | null>(null);
  readonly adminStatuses = signal<AdminEmployeeStatus[]>([]);
  readonly kimaiUsers = signal<KimaiUser[]>([]);
  readonly adminMessage = signal('');
  readonly adminBusy = signal(false);
  readonly adminDirty = signal(false);

  loadAdminSettings(): void {
    this.adminBusy.set(true);
    this.adminApi.getSettings(this.adminPassword()).subscribe({
      next: settings => {
        this.adminSettings.set(this.withEditableTokens(settings));
        this.adminMessage.set('');
        this.adminDirty.set(false);
        this.adminBusy.set(false);
        this.loadAdminEmployeeStatuses();
      },
      error: (error: HttpErrorResponse) => {
        this.adminMessage.set(this.adminLoginErrorMessage(error));
        this.adminBusy.set(false);
      },
    });
  }

  saveAdminSettings(): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    if (this.findDuplicatePin(settings)) {
      this.adminMessage.set('PINs muessen eindeutig sein.');
      return;
    }

    this.adminBusy.set(true);
    this.adminApi.saveSettings(this.adminPassword(), this.toUpdatePayload(settings)).subscribe({
      next: saved => {
        this.adminSettings.set(this.withEditableTokens(saved));
        this.adminMessage.set('Gespeichert');
        this.adminDirty.set(false);
        this.adminBusy.set(false);
        this.loadAdminEmployeeStatuses();
      },
      error: (error: HttpErrorResponse) => {
        this.adminMessage.set(error.status === 409 ? 'PINs muessen eindeutig sein.' : 'Speichern fehlgeschlagen');
        this.adminBusy.set(false);
      },
    });
  }

  importKimaiUsers(): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    this.adminBusy.set(true);
    this.adminApi.importKimaiUsers(this.adminPassword(), settings.baseUrl).subscribe({
      next: users => {
        this.kimaiUsers.set(users);
        const added = this.mergeKimaiUsers(users);
        this.adminMessage.set(`${users.length} Kimai-Mitarbeiter geladen, ${added} uebernommen`);
        this.adminBusy.set(false);
      },
      error: () => {
        this.adminMessage.set('Kimai-Mitarbeiter konnten nicht geladen werden');
        this.adminBusy.set(false);
      },
    });
  }

  addKimaiUser(user: KimaiUser): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    if (this.hasKimaiUser(settings, user)) {
      this.adminMessage.set('Mitarbeiter ist bereits uebernommen');
      return;
    }

    this.updateSettings(current => ({
      ...current,
      employees: [...current.employees, this.createEmployeeFromKimaiUser(user, current.employees.length)],
    }));
  }

  isKimaiUserConfigured(user: KimaiUser): boolean {
    const settings = this.adminSettings();
    return settings ? this.hasKimaiUser(settings, user) : false;
  }

  statusFor(employeeId: string): AdminEmployeeStatus | null {
    return this.adminStatuses().find(status => status.employeeId === employeeId) ?? null;
  }

  addEmployee(): void {
    this.updateSettings(settings => ({
      ...settings,
      employees: [
        ...settings.employees,
        {
          id: crypto.randomUUID(),
          kimaiUserId: null,
          displayName: 'Neuer Mitarbeiter',
          pin: null,
          hasApiToken: false,
          apiToken: '',
          projectId: null,
          activityId: null,
          color: this.nextColor(settings.employees.length),
          imageUrl: null,
          description: 'Arbeitszeit',
          tags: ['stempeluhr'],
          billable: true,
          isEnabled: true,
        },
      ],
    }));
  }

  removeEmployee(index: number): void {
    this.updateSettings(settings => ({
      ...settings,
      employees: settings.employees.filter((_, employeeIndex) => employeeIndex !== index),
    }));
  }

  updateBaseUrl(value: string): void {
    this.updateSettings(settings => ({ ...settings, baseUrl: value }));
  }

  updateAdminPassword(value: string): void {
    this.updateSettings(settings => ({ ...settings, adminPassword: value }) as AdminSettings);
  }

  updateAdminApiToken(value: string): void {
    this.updateSettings(settings => ({ ...settings, adminApiToken: value }) as AdminSettings);
  }

  updateDefaultProjectId(value: string): void {
    this.updateSettings(settings => ({ ...settings, defaultProjectId: this.toNumber(value) }));
  }

  updateDefaultActivityId(value: string): void {
    this.updateSettings(settings => ({ ...settings, defaultActivityId: this.toNumber(value) }));
  }

  updatePauseActivityId(value: string): void {
    this.updateSettings(settings => ({ ...settings, pauseActivityId: this.toNumber(value) }));
  }

  updateEmployee(index: number, patch: Partial<AdminEmployee>): void {
    this.updateSettings(settings => ({
      ...settings,
      employees: settings.employees.map((employee, employeeIndex) =>
        employeeIndex === index ? { ...employee, ...patch } : employee),
    }));
  }

  updateEmployeeTags(index: number, value: string): void {
    this.updateEmployee(index, {
      tags: value.split(',').map(tag => tag.trim()).filter(Boolean),
    });
  }

  setEmployeeImage(index: number, event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    const reader = new FileReader();
    reader.onload = () => this.updateEmployee(index, { imageUrl: String(reader.result) });
    reader.readAsDataURL(file);
  }

  clearEmployeeImage(index: number): void {
    this.updateEmployee(index, { imageUrl: null });
  }

  private loadAdminEmployeeStatuses(): void {
    this.adminApi.getEmployeeStatuses(this.adminPassword()).subscribe({
      next: statuses => this.adminStatuses.set(statuses),
      error: () => this.adminStatuses.set([]),
    });
  }

  private adminLoginErrorMessage(error: HttpErrorResponse): string {
    if (error.status === 0 || error.status === 404) {
      return 'Backend nicht erreichbar. Lokal bitte .NET auf Port 5100 starten und Angular mit Proxy verwenden.';
    }

    if (error.status === 401) {
      return 'Admin-Passwort stimmt nicht oder ist noch nicht gesetzt.';
    }

    return `Admin-Anmeldung fehlgeschlagen (${error.status}).`;
  }

  private withEditableTokens(settings: AdminSettings): AdminSettings {
    return {
      ...settings,
      employees: settings.employees.map(employee => ({ ...employee, apiToken: '' })),
    };
  }

  private toUpdatePayload(settings: AdminSettings): unknown {
    const extended = settings as AdminSettings & { adminPassword?: string; adminApiToken?: string };

    return {
      baseUrl: settings.baseUrl,
      adminPassword: extended.adminPassword ?? '',
      adminApiToken: extended.adminApiToken ?? '',
      keepAdminApiToken: settings.hasAdminApiToken && !extended.adminApiToken,
      defaultProjectId: settings.defaultProjectId,
      defaultActivityId: settings.defaultActivityId,
      pauseActivityId: settings.pauseActivityId,
      employees: settings.employees.map(employee => ({
        id: employee.id,
        kimaiUserId: employee.kimaiUserId,
        displayName: employee.displayName,
        pin: employee.pin,
        apiToken: employee.apiToken ?? '',
        keepApiToken: employee.hasApiToken && !employee.apiToken,
        projectId: employee.projectId,
        activityId: employee.activityId,
        color: employee.color,
        imageUrl: employee.imageUrl,
        description: employee.description,
        tags: employee.tags,
        billable: employee.billable,
        isEnabled: employee.isEnabled,
      })),
    };
  }

  private findDuplicatePin(settings: AdminSettings): boolean {
    const pins = new Set<string>();
    for (const employee of settings.employees) {
      const pin = employee.pin?.trim();
      if (!pin) {
        continue;
      }

      if (pins.has(pin)) {
        return true;
      }

      pins.add(pin);
    }

    return false;
  }

  private updateSettings(update: (settings: AdminSettings) => AdminSettings): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    this.adminSettings.set(update(settings));
    this.adminDirty.set(true);
  }

  private mergeKimaiUsers(users: KimaiUser[]): number {
    let added = 0;
    const settings = this.adminSettings();
    if (!settings) {
      return 0;
    }

    const employees = [...settings.employees];
    for (const user of users) {
      if (employees.some(employee => employee.kimaiUserId === user.id)) {
        continue;
      }

      employees.push(this.createEmployeeFromKimaiUser(user, employees.length));
      added++;
    }

    if (added > 0) {
      this.updateSettings(current => ({ ...current, employees }));
    }

    return added;
  }

  private hasKimaiUser(settings: AdminSettings, user: KimaiUser): boolean {
    return settings.employees.some(employee => employee.kimaiUserId === user.id);
  }

  private createEmployeeFromKimaiUser(user: KimaiUser, index: number): AdminEmployee {
    return {
      id: crypto.randomUUID(),
      kimaiUserId: user.id,
      displayName: user.displayName,
      pin: null,
      hasApiToken: false,
      apiToken: '',
      projectId: null,
      activityId: null,
      color: this.nextColor(index),
      imageUrl: user.avatarUrl,
      description: 'Arbeitszeit',
      tags: ['stempeluhr'],
      billable: true,
      isEnabled: true,
    };
  }

  private toNumber(value: string): number | null {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
  }

  private nextColor(index: number): string {
    const colors = ['#2f7d57', '#204ecf', '#b45309', '#a13768', '#5b6375', '#0f766e'];
    return colors[index % colors.length];
  }
}
