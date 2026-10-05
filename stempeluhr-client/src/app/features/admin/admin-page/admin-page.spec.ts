import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminSettings } from '../../../core/models/admin.models';
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
    http.expectOne(request => request.url === '/api/nfc/events/latest').flush({ event: null });
  }

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
      expect(component.telegramApproverText(component.adminSettings()!)).toBe('33, 44;55  33');
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

    it('does not save approver ids that are not numbers', async () => {
      const { fixture, component, http } = await openPage(withTelegram);

      component.updateTelegramApproverUserIds('11, @chef');
      component.saveAdminSettings();

      http.expectNone('/api/admin/settings');
      expect(component.adminMessage()).toContain('Telegram-User-IDs müssen Zahlen sein');
      fixture.destroy();
    });

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
    fixture.detectChanges();

    const selects = page.querySelectorAll<HTMLSelectElement>('#booking select');
    expect(Array.from(selects, select => select.value)).toEqual(['42', '73', '74']);
    expect(page.querySelector('.loading-note')).toBeNull();
    expect(page.querySelector<HTMLFieldSetElement>('.admin-editor')?.disabled).toBe(false);
    http.expectOne('/api/health').flush({});
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
    fixture.detectChanges();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector<HTMLFieldSetElement>('.admin-editor')?.disabled).toBe(false);
    const selects = page.querySelectorAll<HTMLSelectElement>('#booking select');
    expect(Array.from(selects, select => select.value)).toEqual(['42', '73', '']);
    expect(fixture.componentInstance.adminMessage()).toContain('Kimai-Projekte oder Aktivitäten');
    http.expectOne('/api/health').flush({});
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
    http.expectOne('/api/health').flush({});
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
});
