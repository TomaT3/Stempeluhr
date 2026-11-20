import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminEmployee, AdminSettings, AdminTerminalStatus } from '../../../core/models/admin.models';
import { NfcClockEvent } from '../../../core/models/kiosk.models';
import { AdminSession } from '../../../core/services/admin-session';
import { AdminPage } from './admin-page';

describe('AdminPage', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  const emptySettings: AdminSettings = {
    baseUrl: 'https://kimai.example.test',
    hasAdminPassword: true,
    hasAdminApiToken: true,
    defaultProjectId: null,
    defaultActivityId: null,
    pauseActivityId: null,
    employees: [],
  };

  async function createPage() {
    localStorage.removeItem('stempeluhr.admin.nfcTerminalId');
    await TestBed.configureTestingModule({
      imports: [AdminPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(AdminPage);
    return { fixture, component: fixture.componentInstance, http: TestBed.inject(HttpTestingController) };
  }

  function flushLogin(
    http: HttpTestingController, password: string, openCorrections: unknown[] = [], settings: AdminSettings = emptySettings,
    openAbsences: unknown[] = [],
  ): void {
    const request = http.expectOne('/api/admin/settings');
    expect(request.request.headers.get('X-Admin-Password')).toBe(password);
    request.flush(settings);
    http.expectOne('/api/admin/kimai-projects').flush([]);
    http.expectOne('/api/admin/kimai-activities').flush([]);
    http.expectOne('/api/admin/employee-statuses').flush([]);
    const corrections = http.expectOne(request => request.url === '/api/admin/corrections');
    expect(corrections.request.params.get('status')).toBe('open');
    corrections.flush(openCorrections);
    const absences = http.expectOne(request => request.url === '/api/admin/absences');
    expect(absences.request.params.get('status')).toBe('open');
    absences.flush(openAbsences);
    flushTerminals(http);
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });
  }

  function terminalStatus(terminalId: string): AdminTerminalStatus {
    return { terminalId, state: 'online', lastReportAt: null, problems: [], report: null, powerFlags: [] };
  }

  function flushTerminals(http: HttpTestingController, terminalIds: string[] = ['stempeluhr-pi-01']): void {
    http.expectOne('/api/admin/terminal-statuses').flush(terminalIds.map(terminalStatus));
  }

  function nfcEvent(terminalId: string): NfcClockEvent {
    return {
      eventId: 'e1', occurredAt: '2026-11-20T08:44:10Z', terminalId, cardId: '04A1B2C3',
      employee: null, status: null, message: 'NFC-Karte erkannt.', success: true,
    };
  }

  describe('NFC-Kartenleser', () => {
    const storageKey = 'stempeluhr.admin.nfcTerminalId';

    /** Login bis vor die Terminalliste; die Seite startet mit der gespeicherten ID `storedTerminalId`. */
    async function loginWithStoredTerminal(storedTerminalId: string) {
      sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
      const page = await createPage();
      localStorage.setItem(storageKey, storedTerminalId);
      page.component.nfcTerminalId.set(storedTerminalId);
      page.http.expectOne('/api/admin/settings').flush(emptySettings);
      page.http.expectOne('/api/admin/kimai-projects').flush([]);
      page.http.expectOne('/api/admin/kimai-activities').flush([]);
      page.http.expectOne('/api/admin/employee-statuses').flush([]);
      page.http.expectOne(request => request.url === '/api/admin/corrections').flush([]);
      page.http.expectOne(request => request.url === '/api/admin/absences').flush([]);
      return page;
    }

    function expectLatestNfcRequest(http: HttpTestingController, terminalId: string, event: NfcClockEvent | null): void {
      const latest = http.expectOne(request => request.url === '/api/nfc/events/latest');
      expect(latest.request.params.get('terminalId')).toBe(terminalId);
      latest.flush({ event });
    }

    function terminalSelect(page: HTMLElement): HTMLSelectElement {
      return page.querySelector<HTMLSelectElement>('#nfc select')!;
    }

    afterEach(() => localStorage.removeItem(storageKey));

    it('offers the registered terminals and replaces an unknown stored terminal with the first one', async () => {
      const { fixture, http } = await loginWithStoredTerminal('vertippt');
      flushTerminals(http, ['stempeluhr-01', 'stempeluhr-02']);
      expectLatestNfcRequest(http, 'stempeluhr-01', null);
      fixture.detectChanges();

      const select = terminalSelect(fixture.nativeElement as HTMLElement);
      expect(Array.from(select.options, option => option.value)).toEqual(['stempeluhr-01', 'stempeluhr-02']);
      expect(select.value).toBe('stempeluhr-01');
      expect(localStorage.getItem(storageKey)).toBe('stempeluhr-01');
      fixture.destroy();
    });

    it('keeps a stored terminal that is registered and shows it selected in its registered spelling', async () => {
      const { fixture, http } = await loginWithStoredTerminal('STEMPELUHR-02');
      flushTerminals(http, ['stempeluhr-01', 'stempeluhr-02']);
      expectLatestNfcRequest(http, 'stempeluhr-02', null);
      fixture.detectChanges();

      expect(terminalSelect(fixture.nativeElement as HTMLElement).value).toBe('stempeluhr-02');
      expect(localStorage.getItem(storageKey)).toBe('stempeluhr-02');
      fixture.destroy();
    });

    it('does not poll when no terminal is registered', async () => {
      const { fixture, component, http } = await loginWithStoredTerminal('stempeluhr-pi-01');
      flushTerminals(http, []);
      fixture.detectChanges();

      http.expectNone(request => request.url === '/api/nfc/events/latest');
      expect(component.nfcMessage()).toContain('Keine Terminals registriert');
      expect(terminalSelect(fixture.nativeElement as HTMLElement).disabled).toBe(true);
      fixture.destroy();
    });

    it('keeps the stored terminal selectable when the terminal list fails', async () => {
      const { fixture, http } = await loginWithStoredTerminal('stempeluhr-02');
      http.expectOne('/api/admin/terminal-statuses').flush('kaputt', { status: 500, statusText: 'Server Error' });
      expectLatestNfcRequest(http, 'stempeluhr-02', null);
      fixture.detectChanges();

      expect(terminalSelect(fixture.nativeElement as HTMLElement).value).toBe('stempeluhr-02');
      fixture.destroy();
    });

    it('shows the scan terminal as read-only text and switches to it on request', async () => {
      const { fixture, component, http } = await loginWithStoredTerminal('stempeluhr-02');
      flushTerminals(http, ['stempeluhr-01', 'stempeluhr-02']);
      expectLatestNfcRequest(http, 'stempeluhr-02', nfcEvent('stempeluhr-01'));
      fixture.detectChanges();

      const page = fixture.nativeElement as HTMLElement;
      expect(page.querySelector('#nfc .nfc-scan-terminal')?.textContent?.trim()).toBe('stempeluhr-01');
      expect(page.querySelectorAll('#nfc input')).toHaveLength(0);
      expect(component.nfcMessage()).toContain('„stempeluhr-01“');

      const switchButton = page.querySelector<HTMLButtonElement>('#nfc .nfc-use-scan-terminal')!;
      expect(switchButton.textContent).toContain('stempeluhr-01');
      switchButton.click();
      expectLatestNfcRequest(http, 'stempeluhr-01', nfcEvent('stempeluhr-01'));
      fixture.detectChanges();

      expect(component.nfcTerminalId()).toBe('stempeluhr-01');
      expect(localStorage.getItem(storageKey)).toBe('stempeluhr-01');
      expect(page.querySelector('#nfc .nfc-use-scan-terminal')).toBeNull();
      fixture.destroy();
    });
  });

  it('shows the number of open correction requests on the quick link', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http } = await createPage();
    flushLogin(http, 'test-password', [{ id: 'a' }, { id: 'b' }]);
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector('a[href="/admin/corrections"]');
    expect(link?.textContent).toContain('Korrekturanträge');
    expect(link?.querySelector('.count-badge')?.textContent?.trim()).toBe('2');
    fixture.destroy();
  });

  it('shows the number of open absence requests on the quick link', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http } = await createPage();
    flushLogin(http, 'test-password', [], emptySettings, [{ id: 'a' }, { id: 'b' }, { id: 'c' }]);
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector('a[href="/admin/absences"]');
    expect(link?.textContent).toContain('Abwesenheiten');
    expect(link?.querySelector('.count-badge')?.textContent?.trim()).toBe('3');
    fixture.destroy();
  });

  it('logs in with Enter in the password field', async () => {
    const { fixture, http } = await createPage();
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;

    const input = page.querySelector<HTMLInputElement>('.admin-login input[type="password"]')!;
    input.value = 'test-password';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));

    flushLogin(http, 'test-password');
    fixture.detectChanges();
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('test-password');
    expect(page.querySelector('.admin-login input')).toBeNull();
    expect(page.querySelector('.admin-editor')).not.toBeNull();
    fixture.destroy();
  });

  it('loads the settings right away when already logged in', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'kept-password');
    const { fixture, http } = await createPage();

    flushLogin(http, 'kept-password');
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.admin-editor')).not.toBeNull();

    fixture.componentInstance.logout();
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(fixture.componentInstance.adminSettings()).toBeNull();
    fixture.destroy();
  });

  describe('Telegram approval settings', () => {
    const withTelegram: AdminSettings = {
      ...emptySettings,
      telegramCorrectionChatId: '-1001234567890',
      telegramApproverUserIds: [11, 22],
    };

    async function openPage(settings: AdminSettings) {
      sessionStorage.setItem(AdminSession.StorageKey, 'pw');
      const page = await createPage();
      flushLogin(page.http, 'pw', [], settings);
      page.fixture.detectChanges();
      return page;
    }

    function savedPayload(http: HttpTestingController): Record<string, unknown> {
      const request = http.expectOne('/api/admin/settings');
      expect(request.request.method).toBe('PUT');
      const body = request.request.body as Record<string, unknown>;
      request.flush(withTelegram);
      http.expectOne('/api/admin/employee-statuses').flush([]);
      return body;
    }

    it('shows the correction chat and the approver ids in their fields', async () => {
      const { fixture } = await openPage(withTelegram);

      const inputs = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('#telegram input'));
      expect(inputs.map(input => input.value)).toEqual(['-1001234567890', '11, 22']);
      fixture.destroy();
    });

    it('sends unchanged values back and edits them as text', async () => {
      const { fixture, component, http } = await openPage(withTelegram);

      component.saveAdminSettings();
      expect(savedPayload(http)).toMatchObject({
        telegramCorrectionChatId: '-1001234567890',
        telegramApproverUserIds: [11, 22],
      });

      component.updateTelegramCorrectionChatId(' -42 ');
      component.updateTelegramApproverUserIds('33, 44;55  33');
      expect(component.telegramApproverText()).toBe('33, 44;55  33');
      component.saveAdminSettings();
      expect(savedPayload(http)).toMatchObject({
        telegramCorrectionChatId: ' -42 ',
        telegramApproverUserIds: [33, 44, 55],
      });
      fixture.destroy();
    });

    it('clears both fields with empty values', async () => {
      const { fixture, component, http } = await openPage(withTelegram);

      component.updateTelegramCorrectionChatId('');
      component.updateTelegramApproverUserIds('');
      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({
        telegramCorrectionChatId: '',
        telegramApproverUserIds: [],
      });
      fixture.destroy();
    });

    for (const input of ['11, @chef', '0', '11, 0', '-5']) {
      it(`does not save approver ids that are not positive numbers ("${input}")`, async () => {
        const { fixture, component, http } = await openPage(withTelegram);

        component.updateTelegramApproverUserIds(input);
        component.saveAdminSettings();

        http.expectNone('/api/admin/settings');
        expect(component.adminMessage()).toContain('Telegram-User-IDs müssen positive Zahlen sein');
        fixture.destroy();
      });
    }

    it('leaves the values alone when the backend does not know the fields', async () => {
      const { fixture, component, http } = await openPage(emptySettings);

      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({
        telegramCorrectionChatId: null,
        telegramApproverUserIds: null,
      });
      fixture.destroy();
    });
  });

  describe('terminal maintenance settings', () => {
    const configured: AdminSettings = {
      ...emptySettings,
      serviceWifiSsid: 'Stempeluhr-Service',
      hasServiceWifiPsk: true,
      hasMaintenancePin: true,
    };

    async function openPage(settings: AdminSettings) {
      sessionStorage.setItem(AdminSession.StorageKey, 'pw');
      const page = await createPage();
      flushLogin(page.http, 'pw', [], settings);
      page.fixture.detectChanges();
      return page;
    }

    function savedPayload(http: HttpTestingController): Record<string, unknown> {
      const request = http.expectOne('/api/admin/settings');
      const body = request.request.body as Record<string, unknown>;
      request.flush(configured);
      http.expectOne('/api/admin/employee-statuses').flush([]);
      return body;
    }

    it('keeps stored secrets when only the name is sent back', async () => {
      const { fixture, component, http } = await openPage(configured);
      const section = (fixture.nativeElement as HTMLElement).querySelector('#maintenance');
      expect(section?.querySelector('input')?.value).toBe('Stempeluhr-Service');
      expect(section?.textContent).toContain('Wartungs-PIN entfernen');

      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({
        serviceWifiSsid: 'Stempeluhr-Service', serviceWifiPsk: '', maintenancePin: '', clearMaintenancePin: false,
      });
      fixture.destroy();
    });

    it('asks for a new password once the name changes', async () => {
      const { fixture, component } = await openPage(configured);
      const passwordInput = () =>
        (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>('#maintenance input')[1];
      expect(passwordInput().placeholder).toContain('leer lassen zum Behalten');

      component.updateMaintenance({ serviceWifiSsid: 'Handy-B' });
      fixture.detectChanges();
      expect(passwordInput().placeholder).toBe('mindestens 8 Zeichen');

      component.updateMaintenance({ serviceWifiSsid: 'Stempeluhr-Service' });
      fixture.detectChanges();
      expect(passwordInput().placeholder).toContain('leer lassen zum Behalten');
      fixture.destroy();
    });

    it('sends a new password and PIN once and forgets them after saving', async () => {
      const { fixture, component, http } = await openPage(configured);

      component.updateMaintenance({ serviceWifiPsk: 'neues-geheim', maintenancePin: '739104' });
      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({ serviceWifiPsk: 'neues-geheim', maintenancePin: '739104' });
      expect(component.adminSettings()?.serviceWifiPsk).toBeUndefined();
      expect(component.adminSettings()?.maintenancePin).toBeUndefined();
      fixture.destroy();
    });

    it('switches the maintenance WLAN off with an empty name and removes the PIN', async () => {
      const { fixture, component, http } = await openPage(configured);

      component.updateMaintenance({ serviceWifiSsid: '', clearMaintenancePin: true });
      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({ serviceWifiSsid: '', clearMaintenancePin: true });
      fixture.destroy();
    });

    it('sends the WPA3-only choice of the hotspot', async () => {
      const { fixture, component, http } = await openPage({ ...configured, serviceWifiWpa3Only: false });
      const toggle = [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLLabelElement>('#maintenance label')]
        .find(label => label.textContent?.includes('nur mit WPA3'))?.querySelector('input');
      expect(toggle?.checked).toBe(false);

      toggle!.click();
      component.saveAdminSettings();

      expect(savedPayload(http)).toMatchObject({ serviceWifiWpa3Only: true });
      fixture.destroy();
    });

    it('hides the section and changes nothing when the backend does not know the fields', async () => {
      const { fixture, component, http } = await openPage(emptySettings);

      expect((fixture.nativeElement as HTMLElement).querySelector('#maintenance')).toBeNull();
      component.saveAdminSettings();

      expect(savedPayload(http))
        .toMatchObject({ serviceWifiSsid: null, serviceWifiPsk: '', serviceWifiWpa3Only: null, maintenancePin: '' });
      fixture.destroy();
    });
  });

  describe('absence settings and contract data', () => {
    const anna: AdminEmployee = {
      id: 'anna',
      kimaiUserId: 7,
      displayName: 'Anna',
      pin: null,
      nfcCardId: null,
      hasApiToken: true,
      projectId: null,
      activityId: null,
      color: '#2f7d57',
      imageUrl: null,
      description: null,
      tags: [],
      billable: true,
      isEnabled: true,
      tasks: [],
      defaultTaskLabel: null,
      personnelNumber: '1007',
      entryDate: '2026-03-15',
      exitDate: '2027-06-30',
      vacationDaysPerYear: 30,
      absenceDayHours: 8,
      vacationYears: [{ year: 2026, carryOverDays: 3.5, correctionDays: 0, note: null }],
    };
    const withAbsences: AdminSettings = {
      ...emptySettings,
      absenceProjectId: 40,
      vacationActivityId: 41,
      sickActivityId: 42,
      flexDayActivityId: 43,
      absenceDayStart: '07:30',
      employees: [anna],
    };

    async function openPage(settings: AdminSettings) {
      sessionStorage.setItem(AdminSession.StorageKey, 'pw');
      const page = await createPage();
      flushLogin(page.http, 'pw', [], settings);
      page.fixture.detectChanges();
      return page;
    }

    function savedPayload(http: HttpTestingController) {
      const request = http.expectOne('/api/admin/settings');
      expect(request.request.method).toBe('PUT');
      const body = request.request.body as Record<string, unknown> & { employees: Record<string, unknown>[] };
      request.flush(withAbsences);
      http.expectOne('/api/admin/employee-statuses').flush([]);
      return body;
    }

    it('shows the stored values in their fields', async () => {
      const { fixture } = await openPage(withAbsences);
      const page = fixture.nativeElement as HTMLElement;

      const selects = Array.from(page.querySelectorAll<HTMLSelectElement>('#absences select'));
      expect(selects.map(select => select.value)).toEqual(['40', '41', '42', '43']);
      expect(page.querySelector<HTMLInputElement>('#absences input[type="time"]')?.value).toBe('07:30');
      const card = page.querySelector<HTMLElement>('.employee-admin-card')!;
      expect(Array.from(card.querySelectorAll<HTMLInputElement>('input[type="date"]'), input => input.value))
        .toEqual(['2026-03-15', '2027-06-30']);
      expect(Array.from(card.querySelectorAll<HTMLInputElement>('input[type="number"]'), input => input.value))
        .toEqual(['30', '8']);
      fixture.destroy();
    });

    it('sends unchanged values back and keeps the vacation accounts', async () => {
      const { fixture, component, http } = await openPage(withAbsences);

      component.saveAdminSettings();

      const body = savedPayload(http);
      expect(body).toMatchObject({
        includesAbsences: true,
        absenceProjectId: 40,
        vacationActivityId: 41,
        sickActivityId: 42,
        flexDayActivityId: 43,
        absenceDayStart: '07:30',
      });
      expect(body.employees[0]).toMatchObject({
        includesContract: true,
        personnelNumber: '1007',
        entryDate: '2026-03-15',
        exitDate: '2027-06-30',
        vacationDaysPerYear: 30,
        absenceDayHours: 8,
        vacationYears: null,
      });
      fixture.destroy();
    });

    it('saves edits and clears fields with null', async () => {
      const { fixture, component, http } = await openPage(withAbsences);
      const card = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.employee-admin-card')!;

      const [entry, exit] = Array.from(card.querySelectorAll<HTMLInputElement>('input[type="date"]'));
      entry.value = '2026-04-01';
      entry.dispatchEvent(new Event('change'));
      exit.value = '';
      exit.dispatchEvent(new Event('change'));
      const [days, hours] = Array.from(card.querySelectorAll<HTMLInputElement>('input[type="number"]'));
      days.value = '0';
      days.dispatchEvent(new Event('input'));
      hours.value = '';
      hours.dispatchEvent(new Event('input'));
      component.updateEmployee(0, { personnelNumber: null });
      component.updateAbsenceActivity('flexDayActivityId', '');
      component.updateAbsenceSettings({ absenceProjectId: null, absenceDayStart: '06:00' });
      component.saveAdminSettings();

      const body = savedPayload(http);
      expect(body).toMatchObject({ absenceProjectId: null, flexDayActivityId: null, vacationActivityId: 41, absenceDayStart: '06:00' });
      expect(body.employees[0]).toMatchObject({
        personnelNumber: null,
        entryDate: '2026-04-01',
        exitDate: null,
        vacationDaysPerYear: 0,
        absenceDayHours: null,
      });
      fixture.destroy();
    });

    it('drops an absence activity that belongs to another project', async () => {
      const { fixture, component } = await openPage(withAbsences);
      component.kimaiActivities.set([
        { id: 41, name: 'Urlaub', parentTitle: null, projectId: null, visible: true },
        { id: 42, name: 'Krank', parentTitle: 'Abwesenheit', projectId: 40, visible: true },
      ]);

      component.updateAbsenceSettings({ absenceProjectId: 50 });

      expect(component.adminSettings()).toMatchObject({ absenceProjectId: 50, vacationActivityId: 41, sickActivityId: null, flexDayActivityId: 43 });
      fixture.destroy();
    });

    it('sends contract data for a newly added employee', async () => {
      const { fixture, component, http } = await openPage(withAbsences);

      component.addEmployee();
      component.saveAdminSettings();

      expect(savedPayload(http).employees[1]).toMatchObject({ includesContract: true, entryDate: null, vacationYears: null });
      fixture.destroy();
    });

    it('leaves the values alone when the backend does not know the fields', async () => {
      const { fixture, component, http } = await openPage({
        ...emptySettings,
        employees: [{ ...anna, personnelNumber: undefined, entryDate: undefined, exitDate: undefined, vacationDaysPerYear: undefined, absenceDayHours: undefined, vacationYears: undefined }],
      });

      component.saveAdminSettings();

      const body = savedPayload(http);
      expect(body).toMatchObject({ includesAbsences: false });
      expect(body.employees[0]).toMatchObject({ includesContract: false, vacationYears: null });
      fixture.destroy();
    });
  });

  it('continues with a newly saved admin password once the backend accepts it', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, component, http } = await createPage();
    flushLogin(http, 'old-password');

    component.updateAdminPassword(' new-password ');
    component.saveAdminSettings();
    http.expectOne('/api/admin/settings').flush(emptySettings);
    const probe = http.expectOne('/api/admin/employee-statuses');
    expect(probe.request.headers.get('X-Admin-Password')).toBe('new-password');
    probe.flush([]);

    expect(component.adminPassword()).toBe('new-password');
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('new-password');
    expect(component.adminBusy()).toBe(false);
    fixture.destroy();
  });

  it('keeps the old password when the configured one takes precedence', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, component, http } = await createPage();
    flushLogin(http, 'old-password');

    component.updateAdminPassword('new-password');
    component.saveAdminSettings();
    http.expectOne('/api/admin/settings').flush(emptySettings);
    http.expectOne('/api/admin/employee-statuses').flush(null, { status: 401, statusText: 'Unauthorized' });

    const reload = http.expectOne('/api/admin/employee-statuses');
    expect(reload.request.headers.get('X-Admin-Password')).toBe('old-password');
    reload.flush([]);
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('old-password');
    expect(component.adminMessage()).toContain('Serverkonfiguration');
    fixture.destroy();
  });

  it('drops the password check when the page is left before it answers', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, component, http } = await createPage();
    flushLogin(http, 'old-password');

    component.updateAdminPassword('new-password');
    component.saveAdminSettings();
    http.expectOne('/api/admin/settings').flush(emptySettings);
    const probe = http.expectOne('/api/admin/employee-statuses');

    fixture.destroy();
    TestBed.inject(AdminSession).clear();

    expect(probe.cancelled).toBe(true);
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
  });

  it('ends the session when the old password is refused after a failed password check', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, component, http } = await createPage();
    flushLogin(http, 'old-password');

    component.updateAdminPassword('new-password');
    component.saveAdminSettings();
    http.expectOne('/api/admin/settings').flush(emptySettings);
    http.expectOne('/api/admin/employee-statuses').error(new ProgressEvent('error'));
    http.expectOne('/api/admin/employee-statuses').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(component.adminPassword()).toBe('');
    expect((fixture.nativeElement as HTMLElement).querySelector('.admin-login input[type="password"]')).not.toBeNull();
    fixture.destroy();
  });

  it('keeps unsaved edits when saving is refused and logs in again by saving with a new entry', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, component, http } = await createPage();
    flushLogin(http, 'old-password');

    component.updateBaseUrl('https://kimai.changed.test');
    component.saveAdminSettings();
    http.expectOne('/api/admin/settings').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(component.adminDirty()).toBe(true);
    expect(component.adminSettings()?.baseUrl).toBe('https://kimai.changed.test');
    expect((fixture.nativeElement as HTMLElement).querySelector('.admin-login input[type="password"]')).not.toBeNull();

    component.adminPassword.set('current-password');
    component.saveAdminSettings();
    const save = http.expectOne('/api/admin/settings');
    expect(save.request.headers.get('X-Admin-Password')).toBe('current-password');
    save.flush({ ...emptySettings, baseUrl: 'https://kimai.changed.test' });
    http.expectOne('/api/admin/employee-statuses').flush([]);

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('current-password');
    expect(component.adminDirty()).toBe(false);
    fixture.destroy();
  });

  it('shows saved selections only after settings and Kimai lists have loaded', async () => {
    localStorage.removeItem('stempeluhr.admin.nfcTerminalId');
    await TestBed.configureTestingModule({
      imports: [AdminPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminPage);
    fixture.componentInstance.adminPassword.set('test-password');
    fixture.componentInstance.loadAdminSettings();
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.admin-editor')).toBeNull();
    expect(page.querySelector('.loading-note')).not.toBeNull();

    const settings: AdminSettings = {
      baseUrl: 'https://kimai.example.test',
      hasAdminPassword: true,
      hasAdminApiToken: true,
      defaultProjectId: 42,
      defaultActivityId: 73,
      pauseActivityId: 74,
      employees: [],
    };
    http.expectOne('/api/admin/settings').flush(settings);
    fixture.detectChanges();
    expect(page.querySelector('.admin-editor')).toBeNull();

    http.expectOne('/api/admin/kimai-projects').flush([
      { id: 42, name: 'Büro', parentTitle: null, customerId: null, visible: true },
    ]);
    fixture.detectChanges();
    expect(page.querySelector('.admin-editor')).toBeNull();

    http.expectOne('/api/admin/kimai-activities').flush([
      { id: 73, name: 'Arbeit', parentTitle: null, projectId: null, visible: true },
      { id: 74, name: 'Pause', parentTitle: null, projectId: null, visible: true },
    ]);
    http.expectOne('/api/admin/employee-statuses').flush([]);
    http.expectOne(request => request.url === '/api/admin/corrections').flush([]);
    http.expectOne(request => request.url === '/api/admin/absences').flush([]);
    fixture.detectChanges();

    const selects = page.querySelectorAll<HTMLSelectElement>('#booking select');
    expect(Array.from(selects, select => select.value)).toEqual(['42', '73', '74']);
    expect(page.querySelector('.loading-note')).toBeNull();
    expect(page.querySelector<HTMLFieldSetElement>('.admin-editor')?.disabled).toBe(false);
    http.expectOne('/api/health').flush({});
    flushTerminals(http);
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });
    http.verify();
  });

  it('shows the editor with saved selections when Kimai lists fail', async () => {
    localStorage.removeItem('stempeluhr.admin.nfcTerminalId');
    await TestBed.configureTestingModule({
      imports: [AdminPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminPage);
    fixture.componentInstance.adminPassword.set('test-password');
    fixture.componentInstance.loadAdminSettings();

    const settings: AdminSettings = {
      baseUrl: '',
      hasAdminPassword: true,
      hasAdminApiToken: false,
      defaultProjectId: 42,
      defaultActivityId: 73,
      pauseActivityId: null,
      employees: [],
    };
    http.expectOne('/api/admin/settings').flush(settings);
    http.expectOne('/api/admin/kimai-projects').flush('Kimai nicht konfiguriert', { status: 400, statusText: 'Bad Request' });
    http.expectOne('/api/admin/kimai-activities').flush('Kimai nicht konfiguriert', { status: 400, statusText: 'Bad Request' });
    http.expectOne('/api/admin/employee-statuses').flush([]);
    http.expectOne(request => request.url === '/api/admin/corrections').flush([]);
    http.expectOne(request => request.url === '/api/admin/absences').flush([]);
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector<HTMLFieldSetElement>('.admin-editor')?.disabled).toBe(false);
    const selects = page.querySelectorAll<HTMLSelectElement>('#booking select');
    expect(Array.from(selects, select => select.value)).toEqual(['42', '73', '']);
    expect(fixture.componentInstance.adminMessage()).toContain('Kimai-Projekte oder Aktivitäten');
    http.expectOne('/api/health').flush({});
    flushTerminals(http);
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });
    http.verify();
  });

  it('keeps unsaved edits when reloading settings fails', async () => {
    localStorage.removeItem('stempeluhr.admin.nfcTerminalId');
    await TestBed.configureTestingModule({
      imports: [AdminPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminPage);
    const component = fixture.componentInstance;
    component.adminPassword.set('test-password');
    component.loadAdminSettings();

    const settings: AdminSettings = {
      baseUrl: 'https://kimai.example.test',
      hasAdminPassword: true,
      hasAdminApiToken: true,
      defaultProjectId: 42,
      defaultActivityId: null,
      pauseActivityId: null,
      employees: [],
    };
    http.expectOne('/api/admin/settings').flush(settings);
    http.expectOne('/api/admin/kimai-projects').flush([
      { id: 42, name: 'Büro', parentTitle: null, customerId: null, visible: true },
      { id: 43, name: 'Werkstatt', parentTitle: null, customerId: null, visible: true },
    ]);
    http.expectOne('/api/admin/kimai-activities').flush([]);
    http.expectOne('/api/admin/employee-statuses').flush([]);
    http.expectOne(request => request.url === '/api/admin/corrections').flush([]);
    http.expectOne(request => request.url === '/api/admin/absences').flush([]);
    http.expectOne('/api/health').flush({});
    flushTerminals(http);
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });

    component.updateDefaultProjectId('43');
    expect(component.adminDirty()).toBe(true);

    component.adminPassword.set('wrong-password');
    component.loadAdminSettings();
    http.expectOne('/api/admin/settings').flush('Unauthorized', { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(component.adminSettings()?.defaultProjectId).toBe(43);
    expect(component.kimaiProjects().map(project => project.id)).toEqual([42, 43]);
    expect(component.adminDirty()).toBe(true);
    expect(component.adminMessage()).toContain('Admin-Passwort');
    expect((fixture.nativeElement as HTMLElement).querySelector('.admin-editor')).not.toBeNull();
    // NFC-Polling läuft nach dem Fehlschlag wieder.
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });
    fixture.destroy();
    http.verify();
  });

  it('scrolls section navigation in place instead of following base-relative anchors', async () => {
    await TestBed.configureTestingModule({
      imports: [AdminPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(AdminPage);
    fixture.componentInstance.adminSettings.set({
      baseUrl: '',
      hasAdminPassword: true,
      hasAdminApiToken: true,
      defaultProjectId: null,
      defaultActivityId: null,
      pauseActivityId: null,
      employees: [],
    });
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.admin-nav a')).toBeNull();
    const section = page.querySelector<HTMLElement>('#booking')!;
    section.scrollIntoView = vi.fn();
    document.body.appendChild(page);
    page.querySelectorAll<HTMLButtonElement>('.admin-nav button')[1].click();
    expect(section.scrollIntoView).toHaveBeenCalled();
    page.remove();
  });

  it('offers the monthly export section and returns to the login when it loses the session', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http } = await createPage();
    flushLogin(http, 'test-password');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('#export h2')?.textContent).toBe('Monatsexport');
    expect(page.querySelector('#export app-monthly-export-section')).not.toBeNull();

    page.querySelector<HTMLButtonElement>('#export button')!.click();
    http.expectOne(request => request.url === '/api/admin/export/month')
      .flush(new Blob([]), { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(page.querySelector('input[type="password"]')).not.toBeNull();
    expect(page.textContent).toContain('Admin-Passwort ungültig. Bitte neu anmelden.');
    fixture.destroy();
  });
});
