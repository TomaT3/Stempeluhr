import { Component, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';

interface Employee {
  id: string;
  displayName: string;
  initials: string;
  color: string;
  imageUrl: string | null;
  requiresPin: boolean;
}

interface ClockStatus {
  isRunning: boolean;
  activeTimesheetId: number | null;
  startedAt: string | null;
  durationSeconds: number;
  stateText: string;
}

interface KioskEmployeeSession {
  employee: Employee;
  status: ClockStatus;
}

interface AdminEmployeeStatus {
  employeeId: string;
  isRunning: boolean;
  startedAt: string | null;
  durationSeconds: number;
  stateText: string;
  isAvailable: boolean;
}

interface AdminSettings {
  baseUrl: string;
  hasAdminPassword: boolean;
  hasAdminApiToken: boolean;
  defaultProjectId: number | null;
  defaultActivityId: number | null;
  employees: AdminEmployee[];
}

interface AdminEmployee {
  id: string;
  kimaiUserId: number | null;
  displayName: string;
  pin: string | null;
  hasApiToken: boolean;
  apiToken?: string;
  projectId: number | null;
  activityId: number | null;
  color: string;
  imageUrl: string | null;
  description: string | null;
  tags: string[];
  billable: boolean;
  isEnabled: boolean;
}

interface KimaiUser {
  id: number;
  username: string | null;
  email: string | null;
  displayName: string;
  avatarUrl: string | null;
}

type ViewMode = 'clock' | 'admin';

@Component({
  selector: 'app-root',
  imports: [],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  private readonly http = inject(HttpClient);

  readonly mode = signal<ViewMode>('clock');
  readonly selectedEmployee = signal<Employee | null>(null);
  readonly status = signal<ClockStatus | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');
  readonly tick = signal(Date.now());

  readonly adminPassword = signal('');
  readonly adminSettings = signal<AdminSettings | null>(null);
  readonly adminStatuses = signal<AdminEmployeeStatus[]>([]);
  readonly kimaiUsers = signal<KimaiUser[]>([]);
  readonly adminMessage = signal('');
  readonly adminBusy = signal(false);
  readonly adminDirty = signal(false);

  readonly elapsed = computed(() => {
    const status = this.status();
    if (!status?.isRunning) {
      return status?.durationSeconds ?? 0;
    }

    return Math.max(0, status.durationSeconds + Math.floor((this.tick() - this.loadedAt) / 1000));
  });

  private loadedAt = Date.now();
  private resetTimer: number | null = null;

  constructor() {
    window.setInterval(() => this.tick.set(Date.now()), 1000);
  }

  openClock(): void {
    this.mode.set('clock');
    this.adminMessage.set('');
    this.back();
  }

  openAdmin(): void {
    this.mode.set('admin');
    this.back();
  }

  pressDigit(digit: string): void {
    if (this.pin().length < 8) {
      this.pin.update(current => current + digit);
    }
  }

  clearPin(): void {
    this.pin.set('');
    this.message.set('');
  }

  confirmPin(): void {
    if (!this.pin()) {
      return;
    }

    this.isBusy.set(true);
    this.message.set('');
    this.http.post<KioskEmployeeSession>('/api/kiosk/pin-login', { pin: this.pin() }).subscribe({
      next: session => {
        this.loadedAt = Date.now();
        this.selectedEmployee.set(session.employee);
        this.status.set(session.status);
        this.isUnlocked.set(true);
        this.message.set('');
        this.isBusy.set(false);
      },
      error: () => {
        this.message.set('PIN nicht gefunden');
        this.pin.set('');
        this.isUnlocked.set(false);
        this.isBusy.set(false);
        this.playBeeps(2);
      }
    });
  }

  start(): void {
    this.sendClockAction('start');
  }

  stop(): void {
    this.sendClockAction('stop');
  }

  back(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
      this.resetTimer = null;
    }

    this.selectedEmployee.set(null);
    this.status.set(null);
    this.pin.set('');
    this.isUnlocked.set(false);
    this.message.set('');
  }

  loadAdminSettings(): void {
    this.adminBusy.set(true);
    this.http.get<AdminSettings>('/api/admin/settings', { headers: this.adminHeaders() }).subscribe({
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
      }
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
    this.http.put<AdminSettings>('/api/admin/settings', this.toUpdatePayload(settings), { headers: this.adminHeaders() }).subscribe({
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
      }
    });
  }

  importKimaiUsers(): void {
    const settings = this.adminSettings();
    if (!settings) {
      return;
    }

    this.adminBusy.set(true);
    this.http.post<KimaiUser[]>('/api/admin/kimai-users', {
      baseUrl: settings.baseUrl,
      adminApiToken: ''
    }, { headers: this.adminHeaders() }).subscribe({
      next: users => {
        this.kimaiUsers.set(users);
        const added = this.mergeKimaiUsers(users);
        this.adminMessage.set(`${users.length} Kimai-Mitarbeiter geladen, ${added} uebernommen`);
        this.adminBusy.set(false);
      },
      error: () => {
        this.adminMessage.set('Kimai-Mitarbeiter konnten nicht geladen werden');
        this.adminBusy.set(false);
      }
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

    this.updateSettings(settings => ({
      ...settings,
      employees: [...settings.employees, this.createEmployeeFromKimaiUser(user, settings.employees.length)]
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
          isEnabled: true
        }
      ]
    }));
  }

  removeEmployee(index: number): void {
    this.updateSettings(settings => ({
      ...settings,
      employees: settings.employees.filter((_, employeeIndex) => employeeIndex !== index)
    }));
  }

  updateBaseUrl(value: string): void {
    this.updateSettings(settings => ({ ...settings, baseUrl: value }));
  }

  updateAdminPassword(value: string): void {
    this.updateSettings(settings => ({ ...settings, adminPassword: value } as AdminSettings & { adminPassword?: string }));
  }

  updateAdminApiToken(value: string): void {
    this.updateSettings(settings => ({ ...settings, adminApiToken: value } as AdminSettings & { adminApiToken?: string }));
  }

  updateDefaultProjectId(value: string): void {
    this.updateSettings(settings => ({ ...settings, defaultProjectId: this.toNumber(value) }));
  }

  updateDefaultActivityId(value: string): void {
    this.updateSettings(settings => ({ ...settings, defaultActivityId: this.toNumber(value) }));
  }

  updateEmployee(index: number, patch: Partial<AdminEmployee>): void {
    this.updateSettings(settings => ({
      ...settings,
      employees: settings.employees.map((employee, employeeIndex) =>
        employeeIndex === index ? { ...employee, ...patch } : employee)
    }));
  }

  updateEmployeeTags(index: number, value: string): void {
    this.updateEmployee(index, {
      tags: value.split(',').map(tag => tag.trim()).filter(Boolean)
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

  formatDuration(seconds: number): string {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const remainingSeconds = seconds % 60;

    return [hours, minutes, remainingSeconds]
      .map(value => value.toString().padStart(2, '0'))
      .join(':');
  }

  private sendClockAction(action: 'start' | 'stop'): void {
    this.isBusy.set(true);
    this.http.post<ClockStatus>('/api/kiosk/clock', this.requestBody(action)).subscribe({
      next: status => {
        this.loadedAt = Date.now();
        this.status.set(status);
        this.message.set(status.stateText);
        this.isBusy.set(false);
        this.playBeeps(1);
        this.scheduleReset();
      },
      error: () => {
        this.message.set('Kimai konnte nicht speichern');
        this.isBusy.set(false);
        this.playBeeps(2);
        this.scheduleReset();
      }
    });
  }

  private requestBody(action: 'start' | 'stop'): { employeeId: string; pin: string; action: 'start' | 'stop' } {
    return {
      employeeId: this.selectedEmployee()?.id ?? '',
      pin: this.pin(),
      action
    };
  }

  private scheduleReset(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    this.resetTimer = window.setTimeout(() => this.back(), 2200);
  }

  private playBeeps(count: number): void {
    const AudioContextType = window.AudioContext;
    if (!AudioContextType) {
      return;
    }

    const context = new AudioContextType();
    for (let index = 0; index < count; index++) {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      const startAt = context.currentTime + index * 0.22;
      const stopAt = startAt + 0.11;

      oscillator.type = 'sine';
      oscillator.frequency.value = count === 1 ? 880 : 360;
      gain.gain.setValueAtTime(0.0001, startAt);
      gain.gain.exponentialRampToValueAtTime(0.2, startAt + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.0001, stopAt);
      oscillator.connect(gain);
      gain.connect(context.destination);
      oscillator.start(startAt);
      oscillator.stop(stopAt);
    }
  }

  private loadAdminEmployeeStatuses(): void {
    this.http.get<AdminEmployeeStatus[]>('/api/admin/employee-statuses', { headers: this.adminHeaders() }).subscribe({
      next: statuses => this.adminStatuses.set(statuses),
      error: () => this.adminStatuses.set([])
    });
  }

  private adminHeaders(): HttpHeaders {
    return new HttpHeaders({ 'X-Admin-Password': this.adminPassword() });
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
      employees: settings.employees.map(employee => ({ ...employee, apiToken: '' }))
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
        isEnabled: employee.isEnabled
      }))
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
      isEnabled: true
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
