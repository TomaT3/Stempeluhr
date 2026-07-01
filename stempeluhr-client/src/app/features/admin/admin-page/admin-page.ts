import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, OnDestroy, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { AdminEmployee, AdminEmployeeStatus, AdminSettings, KimaiActivity, KimaiProject, KimaiUser } from '../../../core/models/admin.models';
import { NfcClockEvent } from '../../../core/models/kiosk.models';
import { AdminApi } from '../../../core/services/admin-api';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';

@Component({
  selector: 'app-admin-page',
  imports: [Avatar, DatePipe, RouterLink, StatusBadge],
  templateUrl: './admin-page.html',
  styleUrl: './admin-page.scss',
})
export class AdminPage implements OnDestroy {
  private static readonly NfcTerminalStorageKey = 'stempeluhr.admin.nfcTerminalId';

  private readonly adminApi = inject(AdminApi);

  readonly adminPassword = signal('');
  readonly adminSettings = signal<AdminSettings | null>(null);
  readonly adminStatuses = signal<AdminEmployeeStatus[]>([]);
  readonly kimaiActivities = signal<KimaiActivity[]>([]);
  readonly kimaiProjects = signal<KimaiProject[]>([]);
  readonly kimaiUsers = signal<KimaiUser[]>([]);
  readonly nfcTerminalId = signal(this.readStoredNfcTerminalId());
  readonly latestNfcEvent = signal<NfcClockEvent | null>(null);
  readonly nfcMessage = signal('');
  readonly nfcRefreshBusy = signal(false);
  readonly adminMessage = signal('');
  readonly adminBusy = signal(false);
  readonly adminDirty = signal(false);

  private nfcPollTimer: number | null = null;

  loadAdminSettings(): void {
    this.adminBusy.set(true);
    this.adminApi.getSettings(this.adminPassword()).subscribe({
      next: settings => {
        this.adminSettings.set(this.withEditableTokens(settings));
        this.adminMessage.set('');
        this.adminDirty.set(false);
        this.adminBusy.set(false);
        this.loadAdminEmployeeStatuses();
        this.loadKimaiActivities(false);
        this.loadKimaiProjects(false);
        this.startNfcPolling();
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

    if (this.findDuplicateNfcCardId(settings)) {
      this.adminMessage.set('NFC-Karten muessen eindeutig sein.');
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
        this.adminMessage.set(error.status === 409 ? this.conflictMessage(error) : 'Speichern fehlgeschlagen');
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
        this.adminMessage.set(`${users.length} Kimai-Mitarbeiter geladen`);
        this.adminBusy.set(false);
      },
      error: () => {
        this.adminMessage.set('Kimai-Mitarbeiter konnten nicht geladen werden');
        this.adminBusy.set(false);
      },
    });
  }

  importKimaiActivities(): void {
    this.loadKimaiActivities(true);
  }

  importKimaiProjects(): void {
    this.loadKimaiProjects(true);
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

  hasKimaiActivity(activityId: number | null): boolean {
    return activityId !== null && this.kimaiActivities().some(activity => activity.id === activityId);
  }

  activitySelectValue(activityId: number | null): string {
    return activityId?.toString() ?? '';
  }

  activityOptionLabel(activity: KimaiActivity): string {
    return activity.parentTitle ? `${activity.parentTitle} - ${activity.name}` : activity.name;
  }

  missingActivityLabel(activityId: number): string {
    return `Gespeicherte Aktivitaet ${activityId}`;
  }

  hasKimaiProject(projectId: number | null): boolean {
    return projectId !== null && this.kimaiProjects().some(project => project.id === projectId);
  }

  projectSelectValue(projectId: number | null): string {
    return projectId?.toString() ?? '';
  }

  projectOptionLabel(project: KimaiProject): string {
    return project.parentTitle ? `${project.parentTitle} - ${project.name}` : project.name;
  }

  missingProjectLabel(projectId: number): string {
    return `Gespeichertes Projekt ${projectId}`;
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
          nfcCardId: null,
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

  updateNfcTerminalId(value: string): void {
    const terminalId = value.trim();
    this.nfcTerminalId.set(terminalId);
    localStorage.setItem(AdminPage.NfcTerminalStorageKey, terminalId);
    this.latestNfcEvent.set(null);
    this.refreshLatestNfcEvent(true);
  }

  refreshLatestNfcEvent(showFeedback = false): void {
    const terminalId = this.nfcTerminalId().trim();
    if (!terminalId) {
      this.latestNfcEvent.set(null);
      this.nfcMessage.set('Terminal-ID fehlt.');
      return;
    }

    if (showFeedback) {
      this.nfcRefreshBusy.set(true);
    }

    this.adminApi.latestNfcEvent(terminalId, true).subscribe({
      next: latest => {
        this.latestNfcEvent.set(latest.event);
        this.nfcMessage.set(this.nfcStatusMessage(latest.event, terminalId));
        this.nfcRefreshBusy.set(false);
      },
      error: () => {
        this.latestNfcEvent.set(null);
        this.nfcMessage.set('NFC-Status konnte nicht geladen werden.');
        this.nfcRefreshBusy.set(false);
      },
    });
  }

  latestNfcCardId(): string | null {
    return this.normalizeNfcCardId(this.latestNfcEvent()?.cardId);
  }

  assignLatestNfcCardId(index: number): void {
    const cardId = this.latestNfcCardId();
    if (!cardId) {
      this.adminMessage.set('Keine NFC-Karte gelesen.');
      return;
    }

    this.updateEmployee(index, { nfcCardId: cardId });
    this.adminMessage.set('NFC-Karte zugewiesen. Bitte speichern.');
  }

  async copyLatestNfcCardId(): Promise<void> {
    const cardId = this.latestNfcCardId();
    if (!cardId) {
      return;
    }

    try {
      await navigator.clipboard.writeText(cardId);
      this.adminMessage.set('NFC-Karten-ID kopiert.');
    } catch {
      this.adminMessage.set('Kopieren nicht erlaubt. Karten-ID kann manuell markiert werden.');
    }
  }

  ngOnDestroy(): void {
    if (this.nfcPollTimer) {
      window.clearInterval(this.nfcPollTimer);
    }
  }

  private loadAdminEmployeeStatuses(): void {
    this.adminApi.getEmployeeStatuses(this.adminPassword()).subscribe({
      next: statuses => this.adminStatuses.set(statuses),
      error: () => this.adminStatuses.set([]),
    });
  }

  private loadKimaiActivities(showMessage: boolean): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    if (showMessage) {
      this.adminBusy.set(true);
    }

    this.adminApi.importKimaiActivities(this.adminPassword(), settings.baseUrl).subscribe({
      next: activities => {
        this.kimaiActivities.set(activities);
        if (showMessage) {
          this.adminMessage.set(`${activities.length} Kimai-Aktivitaeten geladen`);
          this.adminBusy.set(false);
        }
      },
      error: () => {
        this.kimaiActivities.set([]);
        if (showMessage) {
          this.adminMessage.set('Kimai-Aktivitaeten konnten nicht geladen werden');
          this.adminBusy.set(false);
        }
      },
    });
  }

  private loadKimaiProjects(showMessage: boolean): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    if (showMessage) {
      this.adminBusy.set(true);
    }

    this.adminApi.importKimaiProjects(this.adminPassword(), settings.baseUrl).subscribe({
      next: projects => {
        this.kimaiProjects.set(projects);
        if (showMessage) {
          this.adminMessage.set(`${projects.length} Kimai-Projekte geladen`);
          this.adminBusy.set(false);
        }
      },
      error: () => {
        this.kimaiProjects.set([]);
        if (showMessage) {
          this.adminMessage.set('Kimai-Projekte konnten nicht geladen werden');
          this.adminBusy.set(false);
        }
      },
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
        nfcCardId: this.normalizeNfcCardId(employee.nfcCardId),
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

  private startNfcPolling(): void {
    if (this.nfcPollTimer) {
      window.clearInterval(this.nfcPollTimer);
    }

    this.refreshLatestNfcEvent();
    this.nfcPollTimer = window.setInterval(() => this.refreshLatestNfcEvent(), 1500);
  }

  private nfcStatusMessage(event: NfcClockEvent | null, requestedTerminalId: string): string {
    if (!event) {
      return 'Noch kein NFC-Scan empfangen. Bitte Karte erneut scannen und Pi-Agent/Token pruefen.';
    }

    if (event.terminalId.trim().toLowerCase() !== requestedTerminalId.trim().toLowerCase()) {
      return `Letzter Scan kam von Terminal "${event.terminalId}". Terminal-ID oben anpassen oder Pi-Agent-Konfiguration pruefen.`;
    }

    if (!event.cardId) {
      return event.message || 'NFC-Scan empfangen, aber keine Karten-ID gelesen.';
    }

    return event.success ? 'NFC-Scan empfangen.' : event.message;
  }

  private findDuplicateNfcCardId(settings: AdminSettings): boolean {
    const cardIds = new Set<string>();
    for (const employee of settings.employees) {
      const cardId = this.normalizeNfcCardId(employee.nfcCardId);
      if (!cardId) {
        continue;
      }

      if (cardIds.has(cardId)) {
        return true;
      }

      cardIds.add(cardId);
    }

    return false;
  }

  private normalizeNfcCardId(cardId: string | null | undefined): string | null {
    const normalized = cardId?.replace(/[^0-9a-f]/gi, '').toUpperCase() ?? '';
    return normalized || null;
  }

  private conflictMessage(error: HttpErrorResponse): string {
    const body = error.error as { message?: string } | null;
    return body?.message ?? 'PINs oder NFC-Karten muessen eindeutig sein.';
  }

  private readStoredNfcTerminalId(): string {
    return localStorage.getItem(AdminPage.NfcTerminalStorageKey) ?? 'stempeluhr-pi-01';
  }

  private updateSettings(update: (settings: AdminSettings) => AdminSettings): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    this.adminSettings.set(update(settings));
    this.adminDirty.set(true);
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
      nfcCardId: null,
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
