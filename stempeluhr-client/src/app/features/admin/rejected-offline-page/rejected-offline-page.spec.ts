import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminRejectedOfflineEvent } from '../../../core/models/admin.models';
import { AdminSession } from '../../../core/services/admin-session';
import { RejectedOfflinePage } from './rejected-offline-page';

describe('RejectedOfflinePage', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  async function setup() {
    await TestBed.configureTestingModule({
      imports: [RejectedOfflinePage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(RejectedOfflinePage);
    return { fixture, http: TestBed.inject(HttpTestingController), page: fixture.nativeElement as HTMLElement };
  }

  it('loads right away with an existing session', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http, page } = await setup();

    const request = http.expectOne('/api/admin/rejected-offline-events');
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    request.flush([]);
    fixture.detectChanges();

    expect(page.querySelector('input[type="password"]')).toBeNull();
    expect(page.textContent).toContain('Keine abgelehnten Offline-Stempel vorhanden.');
    fixture.destroy();
  });

  it('asks for the password again when resolving is refused', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http, page } = await setup();
    const entry: AdminRejectedOfflineEvent = {
      eventId: 'evt-1', employeeId: 'emp-1', employeeName: 'Anna', action: 'start',
      performedAt: '2026-10-01T08:00:00Z', rejectedAt: '2026-10-01T09:00:00Z', message: 'abgelehnt', resolvedAt: null,
    };
    http.expectOne('/api/admin/rejected-offline-events').flush([entry]);

    fixture.componentInstance.resolve(entry);
    http.expectOne('/api/admin/rejected-offline-events/evt-1/resolved')
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(page.querySelector('input[type="password"]')).not.toBeNull();
    expect(page.textContent).toContain('Admin-Passwort stimmt nicht.');
    fixture.destroy();
  });
});
