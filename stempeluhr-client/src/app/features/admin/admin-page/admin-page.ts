import { HttpErrorResponse } from '@angular/common/http';
import { DatePipe } from '@angular/common';
import { Component, DestroyRef, OnDestroy, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of, switchMap } from 'rxjs';

import { AdminEmployee, AdminEmployeeStatus, AdminEmployeeTask, AdminSettings, KimaiActivity, KimaiProject, KimaiUser } from '../../../core/models/admin.models';
import { NfcClockEvent } from '../../../core/models/kiosk.models';
import { AdminApi } from '../../../core/services/admin-api';
import { AdminSession } from '../../../core/services/admin-session';
import { Avatar } from '../../../shared/components/avatar/avatar';
import { StatusBadge } from '../../../shared/components/status-badge/status-badge';
import { VersionBadge } from '../../../shared/components/version-badge/version-badge';

@Component({
  selector: 'app-admin-page',
  imports: [Avatar, DatePipe, RouterLink, StatusBadge, VersionBadge],
  templateUrl: './admin-page.html',
  styleUrl: './admin-page.scss',
})
export class AdminPage implements OnDestroy {
  private static readonly NfcTerminalStorageKey = 'stempeluhr.admin.nfcTerminalId';

  private readonly adminApi = inject(AdminApi);
  private readonly destroyRef = inject(DestroyRef);

  readonly adminSession = inject(AdminSession);
  readonly adminPassword = signal(this.adminSession.password());
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
  readonly initialLoading = signal(false);

  private nfcPollTimer: number | null = null;

  constructor() {
    if (this.adminSession.isLoggedIn()) {
      this.loadAdminSettings();
    }
  }

  loadAdminSettings(): void {
    const password = this.adminPassword().trim();
    if (!password) {
      this.adminMessage.set('Bitte Admin-Passwort eingeben.');
      return;
    }

    // Schlägt das Neuladen fehl (falsches Passwort, Netzwerk), wird der
    // bisherige Stand samt ungespeicherter Eingaben wiederhergestellt.
    const previous = {
      settings: this.adminSettings(),
      projects: this.kimaiProjects(),
      activities: this.kimaiActivities(),
      dirty: this.adminDirty(),
      polling: this.nfcPollTimer !== null,
    };
    this.stopNfcPolling();
    this.adminBusy.set(true);
    this.initialLoading.set(true);
    this.adminSettings.set(null);
    this.kimaiProjects.set([]);
    this.kimaiActivities.set([]);
    this.adminMessage.set('');
    this.adminApi.getSettings(password).pipe(
      // Kimai-Fehler blockieren den Editor nicht: Ohne gültige URL/Token muss
      // der Admin genau diese Felder erreichen können. Gespeicherte Auswahlen
      // bleiben über die Fallback-Optionen der Selects sichtbar.
      switchMap(settings => forkJoin({
        settings: of(settings),
        projects: this.adminApi.importKimaiProjects(password, settings.baseUrl).pipe(catchError(() => of(null))),
        activities: this.adminApi.importKimaiActivities(password, settings.baseUrl).pipe(catchError(() => of(null))),
      })),
      // Nach dem Verlassen der Seite darf keine späte Antwort mehr das NFC-Polling starten.
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: ({ settings, projects, activities }) => {
        this.adminSession.remember(password);
        this.adminPassword.set(password);
        this.kimaiProjects.set(projects ?? []);
        this.kimaiActivities.set(activities ?? []);
        this.adminSettings.set(this.withEditableTokens(settings));
        this.adminMessage.set(projects === null || activities === null
          ? 'Kimai-Projekte oder Aktivitäten konnten nicht geladen werden. Bitte Verbindung und Token prüfen und die Listen aktualisieren.'
          : '');
        this.adminDirty.set(false);
        this.adminBusy.set(false);
        this.initialLoading.set(false);
        this.loadAdminEmployeeStatuses();
        this.startNfcPolling();
      },
      error: (error: HttpErrorResponse) => {
        this.adminSettings.set(previous.settings);
        this.kimaiProjects.set(previous.projects);
        this.kimaiActivities.set(previous.activities);
        this.adminDirty.set(previous.dirty);
        if (error.status === 401) {
          this.adminSession.clear();
          this.adminPassword.set('');
        }
        this.adminMessage.set(this.adminLoginErrorMessage(error));
        this.adminBusy.set(false);
        this.initialLoading.set(false);
        if (previous.polling) {
          this.startNfcPolling();
        }
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

    const newPassword = ((settings as AdminSettings & { adminPassword?: string }).adminPassword ?? '').trim();
    this.adminBusy.set(true);
    this.adminApi.saveSettings(this.adminPassword(), this.toUpdatePayload(settings)).subscribe({
      next: saved => {
        this.adminSettings.set(this.withEditableTokens(saved));
        this.adminMessage.set('Gespeichert');
        this.adminDirty.set(false);
        if (newPassword && newPassword !== this.adminPassword()) {
          this.adoptNewAdminPassword(newPassword);
          return;
        }

        this.adminBusy.set(false);
        this.loadAdminEmployeeStatuses();
      },
      error: (error: HttpErrorResponse) => {
        this.adminMessage.set(error.status === 409 || error.status === 400 ? this.conflictMessage(error) : 'Speichern fehlgeschlagen');
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

  // Echte Anker würden wegen <base href="/"> auf /#… statt /admin#… zeigen und
  // die Seite samt ungespeicherter Änderungen verlassen.
  scrollToSection(id: string): void {
    document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
          tasks: [],
          defaultTaskLabel: null,
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

  addEmployeeTask(index: number): void {
    const employee = this.adminSettings()?.employees[index];
    if (!employee) {
      return;
    }

    this.updateEmployee(index, {
      tasks: [
        ...(employee.tasks ?? []),
        { id: crypto.randomUUID().replaceAll('-', ''), label: '', projectId: null, activityId: null, billable: true },
      ],
    });
  }

  updateEmployeeTask(index: number, taskIndex: number, patch: Partial<AdminEmployeeTask>): void {
    const employee = this.adminSettings()?.employees[index];
    if (!employee) {
      return;
    }

    this.updateEmployee(index, {
      tasks: (employee.tasks ?? []).map((task, i) => {
        if (i !== taskIndex) {
          return task;
        }

        const updated = { ...task, ...patch };
        // A project-bound activity of another project would make Kimai
        // reject the start AFTER the switch already stopped the running sheet.
        return this.isActivityAllowedForProject(updated.activityId, updated.projectId)
          ? updated
          : { ...updated, activityId: null };
      }),
    });
  }

  /** Aktivitäten, die Kimai für dieses Projekt akzeptiert: globale plus die des Projekts. */
  activitiesForProject(projectId: number | null): KimaiActivity[] {
    return projectId === null
      ? this.kimaiActivities()
      : this.kimaiActivities().filter(activity => activity.projectId === null || activity.projectId === projectId);
  }

  hasActivityForProject(activityId: number | null, projectId: number | null): boolean {
    return activityId !== null && this.activitiesForProject(projectId).some(activity => activity.id === activityId);
  }

  private isActivityAllowedForProject(activityId: number | null, projectId: number | null): boolean {
    // Unknown activities (list not loaded) stay untouched.
    const activity = this.kimaiActivities().find(candidate => candidate.id === activityId);
    return !activity || projectId === null || activity.projectId === null || activity.projectId === projectId;
  }

  removeEmployeeTask(index: number, taskIndex: number): void {
    const employee = this.adminSettings()?.employees[index];
    if (!employee) {
      return;
    }

    this.updateEmployee(index, {
      tasks: (employee.tasks ?? []).filter((_, i) => i !== taskIndex),
    });
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

  logout(): void {
    if (this.adminDirty() && !window.confirm('Ungespeicherte Änderungen verwerfen und abmelden?')) {
      return;
    }

    this.stopNfcPolling();
    this.adminSession.clear();
    this.adminPassword.set('');
    this.adminSettings.set(null);
    this.adminStatuses.set([]);
    this.kimaiProjects.set([]);
    this.kimaiActivities.set([]);
    this.kimaiUsers.set([]);
    this.latestNfcEvent.set(null);
    this.nfcMessage.set('');
    this.adminMessage.set('');
    this.adminDirty.set(false);
  }

  ngOnDestroy(): void {
    this.stopNfcPolling();
  }

  /**
   * Gilt das neue Passwort, laufen alle weiteren Aufrufe damit. Ein per
   * Konfiguration (Admin__Password) gesetztes Passwort hat beim Backend
   * Vorrang - dann bleibt das bisherige gültig und die Sitzung unverändert.
   */
  private adoptNewAdminPassword(newPassword: string): void {
    this.adminApi.getEmployeeStatuses(newPassword).subscribe({
      next: statuses => {
        this.adminSession.remember(newPassword);
        this.adminPassword.set(newPassword);
        this.adminStatuses.set(statuses);
        this.adminBusy.set(false);
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 401) {
          this.adminMessage.set('Gespeichert. Das Admin-Passwort aus der Serverkonfiguration hat Vorrang und bleibt gültig.');
        }
        this.adminBusy.set(false);
        this.loadAdminEmployeeStatuses();
      },
    });
  }

  private stopNfcPolling(): void {
    if (this.nfcPollTimer !== null) {
      window.clearInterval(this.nfcPollTimer);
      this.nfcPollTimer = null;
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
        // Leerer String löscht die Bezeichnung (null hiesse "unverändert lassen").
        defaultTaskLabel: employee.defaultTaskLabel ?? '',
        tasks: (employee.tasks ?? []).map(task => ({
          id: task.id,
          label: task.label,
          projectId: task.projectId,
          activityId: task.activityId,
          billable: task.billable,
        })),
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
      tasks: [],
      defaultTaskLabel: null,
    };
  }

  toNumber(value: string): number | null {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
  }

  private nextColor(index: number): string {
    const colors = ['#2f7d57', '#204ecf', '#b45309', '#a13768', '#5b6375', '#0f766e'];
    return colors[index % colors.length];
  }
}
