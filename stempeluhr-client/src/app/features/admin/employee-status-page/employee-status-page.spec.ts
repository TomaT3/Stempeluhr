import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminSession } from '../../../core/services/admin-session';
import { EmployeeStatusPage } from './employee-status-page';

describe('EmployeeStatusPage', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  async function setup() {
    await TestBed.configureTestingModule({
      imports: [EmployeeStatusPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(EmployeeStatusPage);
    return { fixture, http: TestBed.inject(HttpTestingController), page: fixture.nativeElement as HTMLElement };
  }

  it('logs in with Enter and keeps the session for the other admin pages', async () => {
    const { fixture, http, page } = await setup();
    fixture.detectChanges();

    const input = page.querySelector<HTMLInputElement>('input[type="password"]')!;
    input.value = 'test-password';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));

    const request = http.expectOne('/api/admin/employee-statuses');
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    request.flush([]);
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('test-password');
    expect(page.querySelector('input[type="password"]')).toBeNull();
    expect(page.textContent).toContain('Als Admin angemeldet');
    fixture.destroy();
  });

  it('loads right away with an existing session and drops it when the password is refused', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'old-password');
    const { fixture, http, page } = await setup();

    const request = http.expectOne('/api/admin/employee-statuses');
    expect(request.request.headers.get('X-Admin-Password')).toBe('old-password');
    request.flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(page.querySelector<HTMLInputElement>('input[type="password"]')?.value).toBe('');
    expect(page.querySelector('.message')?.textContent).toContain('Admin-Passwort stimmt nicht.');
    fixture.destroy();
  });

  it('ignores a late answer after logout', async () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    const { fixture, http } = await setup();
    const request = http.expectOne('/api/admin/employee-statuses');

    fixture.componentInstance.logout();

    expect(request.cancelled).toBe(true);
    expect(TestBed.inject(AdminSession).isLoggedIn()).toBe(false);
    fixture.destroy();
  });
});
