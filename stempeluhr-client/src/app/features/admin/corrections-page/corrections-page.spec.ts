import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminTimeCorrection } from '../../../core/models/admin.models';
import { AdminSession } from '../../../core/services/admin-session';
import { CorrectionsPage } from './corrections-page';

describe('CorrectionsPage', () => {
  beforeEach(() => sessionStorage.clear());
  afterEach(() => sessionStorage.clear());

  function correction(overrides: Partial<AdminTimeCorrection> = {}): AdminTimeCorrection {
    return {
      id: 'c1', employeeId: 'emp-1', employeeName: 'Anna', kind: 'addPause', status: 'pending', source: 'terminal-1',
      createdAt: '2026-10-05T07:00:00Z', comment: null, timeZone: 'Europe/Berlin', timesheetId: 17,
      begin: null, end: null, pauseBegin: '2026-10-04T12:00', pauseEnd: '2026-10-04T12:30',
      taskId: null, taskLabel: null,
      original: { begin: '2026-10-04T08:00', end: '2026-10-04T16:00', description: null },
      decidedAt: null, decidedBy: null, decisionNote: null, error: null, appliedSteps: [],
      ...overrides,
    };
  }

  async function setup(loggedIn = true) {
    if (loggedIn) {
      sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    }
    await TestBed.configureTestingModule({
      imports: [CorrectionsPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(CorrectionsPage);
    const http = TestBed.inject(HttpTestingController);
    return { fixture, http, component: fixture.componentInstance, page: fixture.nativeElement as HTMLElement };
  }

  function expectList(http: HttpTestingController, status: 'open' | 'all'): TestRequest {
    const request = http.expectOne(candidate => candidate.url === '/api/admin/corrections');
    expect(request.request.params.get('status')).toBe(status);
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    return request;
  }

  function button(page: HTMLElement, text: string): HTMLButtonElement {
    const found = Array.from(page.querySelectorAll('button')).find(candidate => candidate.textContent?.trim() === text);
    expect(found).toBeDefined();
    return found!;
  }

  it('loads the open requests right away and shows before and after', async () => {
    const { fixture, http, page } = await setup();

    expectList(http, 'open').flush([correction({ comment: 'Pause vergessen' })]);
    fixture.detectChanges();

    const text = page.textContent ?? '';
    expect(page.querySelector('input[type="password"]')).toBeNull();
    expect(text).toContain('Anna');
    expect(text).toContain('Pause nachtragen');
    expect(text).toContain('Arbeit So 04.10.2026 08:00–16:00');
    expect(text).toContain('Arbeit So 04.10.2026 08:00–12:00');
    expect(text).toContain('Pause So 04.10.2026 12:00–12:30');
    expect(text).toContain('Arbeit So 04.10.2026 12:30–16:00');
    expect(text).toContain('Pause vergessen');
    expect(text).toContain('Terminal terminal-1');
    http.verify();
    fixture.destroy();
  });

  it('shows night shifts across midnight with both dates', async () => {
    const { fixture, http, page } = await setup();

    expectList(http, 'open').flush([correction({
      kind: 'addShift', source: 'clock', timesheetId: null, original: null, pauseBegin: null, pauseEnd: null,
      begin: '2026-10-04T22:00', end: '2026-10-05T06:00', taskLabel: 'Rezeption',
    })]);
    fixture.detectChanges();

    const text = page.textContent ?? '';
    expect(text).toContain('Rezeption So 04.10.2026 22:00 – Mo 05.10.2026 06:00');
    expect(text).toContain('kein Eintrag');
    expect(text).toContain('/clock');
    fixture.destroy();
  });

  it('switches between open and all requests', async () => {
    const { fixture, http, page } = await setup();
    expectList(http, 'open').flush([]);
    fixture.detectChanges();
    expect(page.textContent).toContain('Keine offenen Korrekturanträge.');

    button(page, 'Alle').click();
    expectList(http, 'all').flush([correction({ status: 'applied', decidedBy: 'Admin', decidedAt: '2026-10-05T08:00:00Z' })]);
    fixture.detectChanges();
    expect(page.textContent).toContain('In Kimai eingetragen');
    expect(page.textContent).toContain('Admin');
    expect(page.querySelector('.actions')).toBeNull();

    button(page, 'Offen').click();
    expectList(http, 'open').flush([]);
    http.verify();
    fixture.destroy();
  });

  it('approves, reloads and shows the result', async () => {
    const { fixture, http, page } = await setup();
    expectList(http, 'open').flush([correction()]);
    fixture.detectChanges();

    button(page, 'Genehmigen').click();
    const approve = http.expectOne('/api/admin/corrections/c1/approve');
    expect(approve.request.method).toBe('POST');
    approve.flush(correction({ status: 'applied' }));
    expectList(http, 'open').flush([]);
    fixture.detectChanges();

    expect(page.textContent).toContain('Anna: In Kimai eingetragen.');
    http.verify();
    fixture.destroy();
  });

  it('shows the Kimai message when approving fails', async () => {
    const { fixture, http, page } = await setup();
    expectList(http, 'open').flush([correction()]);
    fixture.detectChanges();

    button(page, 'Genehmigen').click();
    const failed = correction({ status: 'failed', error: 'Sperrzeitraum', appliedSteps: ['shorten'] });
    http.expectOne('/api/admin/corrections/c1/approve').flush(failed);
    expectList(http, 'open').flush([failed]);
    fixture.detectChanges();

    const text = page.textContent ?? '';
    expect(text).toContain('Kimai hat nicht gebucht – Sperrzeitraum');
    expect(text).toContain('1 Schritt(e) schon in Kimai');
    expect(button(page, 'Erneut versuchen')).toBeTruthy();
    expect(button(page, 'Manuell in Kimai erledigt')).toBeTruthy();
    fixture.destroy();
  });

  it('shows the reason field only after clicking reject and sends the reason', async () => {
    const { fixture, http, page } = await setup();
    expectList(http, 'open').flush([correction()]);
    fixture.detectChanges();
    expect(page.querySelector('.reject input')).toBeNull();

    button(page, 'Ablehnen').click();
    fixture.detectChanges();
    const input = page.querySelector<HTMLInputElement>('.reject input')!;
    input.value = '  War doch so  ';
    input.dispatchEvent(new Event('input'));
    button(page, 'Ablehnung senden').click();

    const reject = http.expectOne('/api/admin/corrections/c1/reject');
    expect(reject.request.body).toEqual({ note: 'War doch so' });
    reject.flush(correction({ status: 'rejected', decisionNote: 'War doch so' }));
    expectList(http, 'open').flush([]);
    fixture.detectChanges();

    expect(page.textContent).toContain('Anna: Antrag abgelehnt.');
    expect(fixture.componentInstance.rejectingId()).toBeNull();
    http.verify();
    fixture.destroy();
  });

  it('retries and resolves failed requests', async () => {
    const { fixture, http, page } = await setup();
    const failed = correction({ status: 'failed', error: 'Sperrzeitraum' });
    expectList(http, 'open').flush([failed]);
    fixture.detectChanges();

    button(page, 'Erneut versuchen').click();
    http.expectOne('/api/admin/corrections/c1/retry').flush(correction({ status: 'applied' }));
    expectList(http, 'open').flush([failed]);
    fixture.detectChanges();
    expect(page.textContent).toContain('Anna: In Kimai eingetragen.');

    button(page, 'Manuell in Kimai erledigt').click();
    const resolve = http.expectOne('/api/admin/corrections/c1/resolved');
    expect(resolve.request.method).toBe('PUT');
    resolve.flush(correction({ status: 'resolvedManually' }));
    expectList(http, 'open').flush([]);
    fixture.detectChanges();
    expect(page.textContent).toContain('Anna: Als manuell erledigt markiert.');
    http.verify();
    fixture.destroy();
  });

  it('shows the server message on a conflict and reloads', async () => {
    const { fixture, http, page } = await setup();
    const failed = correction({ status: 'failed' });
    expectList(http, 'open').flush([failed]);
    fixture.detectChanges();

    button(page, 'Erneut versuchen').click();
    http.expectOne('/api/admin/corrections/c1/retry').flush(
      { message: 'Nur fehlgeschlagene Anträge können erneut versucht werden.' },
      { status: 409, statusText: 'Conflict' });
    expectList(http, 'open').flush([]);
    fixture.detectChanges();

    expect(page.textContent).toContain('Nur fehlgeschlagene Anträge können erneut versucht werden.');
    http.verify();
    fixture.destroy();
  });

  it('does not send a second request on double clicks while busy', async () => {
    const { fixture, http, component } = await setup();
    expectList(http, 'open').flush([correction()]);
    fixture.detectChanges();

    const entry = component.entries()[0];
    component.approve(entry);
    component.approve(entry);
    component.retry(entry);
    component.load();
    expect(component.isBusy()).toBe(true);
    http.expectOne('/api/admin/corrections/c1/approve').flush(correction({ status: 'applied' }));
    expectList(http, 'open').flush([]);
    http.verify();
    fixture.destroy();
  });

  it('forgets the session on 401 and asks for the password again', async () => {
    const { fixture, http, page } = await setup();
    expectList(http, 'open').flush([correction()]);
    fixture.detectChanges();

    button(page, 'Genehmigen').click();
    http.expectOne('/api/admin/corrections/c1/approve').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(page.querySelector('input[type="password"]')).not.toBeNull();
    expect(page.textContent).toContain('Admin-Passwort stimmt nicht.');
    http.verify();
    fixture.destroy();
  });

  it('logs in with Enter in the password field', async () => {
    const { fixture, http, page } = await setup(false);
    fixture.detectChanges();
    http.verify();

    const input = page.querySelector<HTMLInputElement>('input[type="password"]')!;
    input.value = 'test-password';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter' }));
    expectList(http, 'open').flush([]);
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('test-password');
    expect(page.querySelector('input[type="password"]')).toBeNull();
    fixture.destroy();
  });
});
