import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminSettings } from '../../../core/models/admin.models';
import { AdminPage } from './admin-page';

describe('AdminPage', () => {
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
