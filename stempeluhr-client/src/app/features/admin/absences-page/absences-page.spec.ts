import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AdminAbsence, AdminEmployee, AdminSettings, AdminVacationBalance } from '../../../core/models/admin.models';
import { AdminSession } from '../../../core/services/admin-session';
import { compactDays } from '../../../shared/absence-format';
import { AbsencesPage, adminWindow } from './absences-page';

describe('AbsencesPage', () => {
  beforeEach(() => {
    sessionStorage.clear();
    // Thursday, 15 October 2026: the calendar starts in October.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 9, 15, 10, 0));
  });
  afterEach(() => {
    sessionStorage.clear();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  function employee(id: string, displayName: string, overrides: Partial<AdminEmployee> = {}): AdminEmployee {
    return {
      id, kimaiUserId: null, displayName, pin: null, nfcCardId: null, hasApiToken: true, projectId: null, activityId: null,
      color: '#2f7d57', imageUrl: null, description: null, tags: [], billable: true, isEnabled: true, tasks: [],
      defaultTaskLabel: null, ...overrides,
    };
  }

  const settings: AdminSettings = {
    baseUrl: 'http://kimai.test', hasAdminPassword: true, hasAdminApiToken: true,
    defaultProjectId: 1, defaultActivityId: 1, pauseActivityId: 2,
    absenceProjectId: 40, vacationActivityId: 41, sickActivityId: 42, flexDayActivityId: 43, absenceDayStart: '08:00',
    employees: [
      employee('anna', 'Anna'),
      employee('max', 'Max'),
      employee('old', 'Ehemalig', { isEnabled: false }),
      employee('notoken', 'Ohne Token', { hasApiToken: false }),
    ],
  };

  function absence(overrides: Partial<AdminAbsence> = {}): AdminAbsence {
    return {
      id: 'a1', employeeId: 'anna', employeeName: 'Anna', kind: 'vacation', status: 'pending',
      days: ['2026-11-02', '2026-11-03', '2026-11-04', '2026-11-05', '2026-11-06', '2026-11-10'],
      source: 'terminal-1', createdAt: '2026-10-14T07:00:00Z', createdBy: 'Anna', comment: null,
      decidedAt: null, decidedBy: null, decisionNote: null, error: null, appliedDays: [],
      cancelledAt: null, cancelledBy: null, applyStartedAt: null,
      ...overrides,
    };
  }

  function balance(employeeId: string, employeeName: string, remaining: number, overrides: Partial<AdminVacationBalance> = {}): AdminVacationBalance {
    return {
      employeeId, employeeName, personnelNumber: null, isEnabled: true, vacationDaysPerYear: 30, note: null,
      balance: { year: 2026, entitlement: 30, carryOver: 0, correction: 0, taken: 30 - remaining - 6, planned: 6, remaining },
      fullMonths: 12, entryDate: '2020-01-01', exitDate: null,
      ...overrides,
    };
  }

  async function setup(loggedIn = true) {
    if (loggedIn) {
      sessionStorage.setItem(AdminSession.StorageKey, 'test-password');
    }
    await TestBed.configureTestingModule({
      imports: [AbsencesPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(AbsencesPage);
    const http = TestBed.inject(HttpTestingController);
    return { fixture, http, component: fixture.componentInstance, page: fixture.nativeElement as HTMLElement };
  }

  function expectAbsences(http: HttpTestingController, status: 'open' | 'all', year: string | null = null): TestRequest {
    const request = http.expectOne(candidate => candidate.url === '/api/admin/absences' && candidate.method === 'GET');
    expect(request.request.params.get('status')).toBe(status);
    expect(request.request.params.get('year')).toBe(year);
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    return request;
  }

  function expectBalances(http: HttpTestingController, year: number): TestRequest {
    return http.expectOne(candidate => candidate.url === '/api/admin/absences/balances' && candidate.params.get('year') === String(year));
  }

  /** Startseite „Anträge“: Einstellungen, Liste und die Urlaubskonten der betroffenen Jahre. */
  function flushRequests(http: HttpTestingController, entries: AdminAbsence[], balances: AdminVacationBalance[] = []): void {
    http.expectOne('/api/admin/settings').flush(settings);
    expectAbsences(http, 'open').flush(entries);
    if (entries.some(entry => entry.kind === 'vacation')) {
      expectBalances(http, 2026).flush(balances);
    }
  }

  function button(page: HTMLElement, text: string): HTMLButtonElement {
    const found = Array.from(page.querySelectorAll('button')).find(candidate => candidate.textContent?.trim() === text);
    expect(found).toBeDefined();
    return found!;
  }

  it('loads the open requests right away and shows days, count and the remainder', async () => {
    const { fixture, http, page } = await setup();

    flushRequests(http, [absence({ comment: 'Familienfeier' })], [balance('anna', 'Anna', 4)]);
    fixture.detectChanges();

    const text = page.textContent ?? '';
    expect(text).toContain('Anna');
    expect(text).toContain('Urlaub');
    expect(text).toContain('Mo 02.–Fr 06.11., Di 10.11.');
    expect(text).toContain('(6 Tage)');
    expect(text).toContain('Rest nach Genehmigung 2026');
    expect(text).toContain('4 Tage');
    expect(text).toContain('Familienfeier');
    expect(text).toContain('Terminal terminal-1');
    expect(page.querySelector('.hint.warning')).toBeNull();
    http.verify();
    fixture.destroy();
  });

  it('approves and reloads the list with the new state', async () => {
    const { fixture, http, page } = await setup();
    flushRequests(http, [absence({ kind: 'sick', days: ['2026-10-14'] })]);
    fixture.detectChanges();

    button(page, 'Genehmigen').click();
    const approve = http.expectOne('/api/admin/absences/a1/approve');
    expect(approve.request.method).toBe('POST');
    approve.flush(absence({ kind: 'sick', status: 'applied', days: ['2026-10-14'] }));
    expectAbsences(http, 'open').flush([]);
    fixture.detectChanges();

    expect(page.querySelector('.message')?.textContent).toContain('Anna: In Kimai eingetragen.');
    expect(page.textContent).toContain('Keine offenen Abwesenheitsanträge.');
    http.verify();
    fixture.destroy();
  });

  it('rejects with an optional note', async () => {
    const { fixture, http, component, page } = await setup();
    flushRequests(http, [absence({ kind: 'flexDay', days: ['2026-10-20'] })]);
    fixture.detectChanges();

    button(page, 'Ablehnen').click();
    fixture.detectChanges();
    component.rejectNote.set('  Bitte tauschen ');
    button(page, 'Ablehnung senden').click();

    const reject = http.expectOne('/api/admin/absences/a1/reject');
    expect(reject.request.body).toEqual({ note: 'Bitte tauschen' });
    reject.flush(absence({ kind: 'flexDay', status: 'rejected', days: ['2026-10-20'] }));
    expectAbsences(http, 'open').flush([]);
    fixture.detectChanges();
    expect(page.querySelector('.message')?.textContent).toContain('Antrag abgelehnt.');
    fixture.destroy();
  });

  it('offers retry and resolve for failed requests and shows the Kimai error and progress', async () => {
    const failed = absence({
      kind: 'sick', status: 'failed', days: ['2026-10-12', '2026-10-13'], appliedDays: ['2026-10-12'],
      error: 'Kimai API returned 403: gesperrt',
    });
    const { fixture, http, page } = await setup();
    flushRequests(http, [failed]);
    fixture.detectChanges();

    expect(page.textContent).toContain('Kimai API returned 403: gesperrt');
    expect(page.textContent).toContain('Schon in Kimai');
    expect(page.textContent).toContain('Mo 12.10.');

    button(page, 'Erneut versuchen').click();
    http.expectOne('/api/admin/absences/a1/retry').flush(failed);
    expectAbsences(http, 'open').flush([failed]);
    fixture.detectChanges();

    button(page, 'Manuell in Kimai erledigt').click();
    const resolve = http.expectOne('/api/admin/absences/a1/resolved');
    expect(resolve.request.method).toBe('PUT');
    resolve.flush({ ...failed, status: 'resolvedManually' });
    expectAbsences(http, 'open').flush([]);
    http.verify();
    fixture.destroy();
  });

  it('cancels applied absences only after confirming how many Kimai entries go', async () => {
    const applied = absence({ kind: 'sick', status: 'applied', days: ['2026-10-12', '2026-10-13'], appliedDays: ['2026-10-12', '2026-10-13'] });
    const { fixture, http, component, page } = await setup();
    flushRequests(http, [applied]);
    fixture.detectChanges();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);

    button(page, 'Stornieren').click();
    http.expectNone('/api/admin/absences/a1/cancel');
    expect(confirm.mock.calls[0][0]).toContain('löscht 2 Einträge in Kimai');

    button(page, 'Stornieren').click();
    http.expectOne('/api/admin/absences/a1/cancel').flush({ ...applied, status: 'cancelled' });
    expectAbsences(http, 'open').flush([]);
    expect(component.message()).toContain('Storniert');
    fixture.destroy();
  });

  it('offers only "Fortsetzen" for an interrupted apply', async () => {
    const { fixture, http, page } = await setup();
    flushRequests(http, [absence({ applyStartedAt: '2026-10-14T08:00:00Z', appliedDays: ['2026-11-02'] })], [balance('anna', 'Anna', 4)]);
    fixture.detectChanges();

    const labels = Array.from(page.querySelectorAll('.entry .actions button'), item => item.textContent?.trim());
    expect(labels).toEqual(['Fortsetzen']);
    expect(page.textContent).toContain('unterbrochen');
    fixture.destroy();
  });

  it('shows the conflict message and the current state when a decision is out of date', async () => {
    const { fixture, http, component, page } = await setup();
    flushRequests(http, [absence({ kind: 'sick', days: ['2026-10-14'] })]);
    fixture.detectChanges();

    button(page, 'Genehmigen').click();
    http.expectOne('/api/admin/absences/a1/approve').flush({ message: 'Antrag nicht gefunden.' }, { status: 404, statusText: 'Not Found' });
    http.expectOne('/api/admin/settings').flush(settings);
    expectAbsences(http, 'open').flush([]);

    expect(component.message()).toBe('Antrag nicht gefunden.');
    fixture.destroy();
  });

  it('forgets the password on 401', async () => {
    const { fixture, http, component, page } = await setup();

    http.expectOne('/api/admin/settings').flush({}, { status: 401, statusText: 'Unauthorized' });
    expectAbsences(http, 'open');
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(component.message()).toBe('Admin-Passwort stimmt nicht.');
    expect(page.querySelector('input[type="password"]')).not.toBeNull();
    expect(page.querySelector('.tabs')).toBeNull();
    fixture.destroy();
  });

  it('forgets the password when only the follow-up balance request is refused', async () => {
    const { fixture, http, component } = await setup();

    http.expectOne('/api/admin/settings').flush(settings);
    expectAbsences(http, 'open').flush([absence()]);
    expectBalances(http, 2026).flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(component.hasLoaded()).toBe(false);
    expect(component.message()).toBe('Admin-Passwort stimmt nicht.');
    fixture.destroy();
  });

  it('offers every year up to the end of the vacation window', async () => {
    const { fixture, http, component, page } = await setup();
    flushRequests(http, []);
    fixture.detectChanges();

    // 15.10.2026 + 18 months = 15.04.2028.
    expect(component.yearOptions).toEqual([2024, 2025, 2026, 2027, 2028]);
    expect(Array.from(page.querySelectorAll('.filter-bar select option'), option => option.textContent?.trim()))
      .toEqual(['Alle Jahre', '2024', '2025', '2026', '2027', '2028']);
    fixture.destroy();
  });

  it('filters by all and by year', async () => {
    const { fixture, http, component } = await setup();
    flushRequests(http, []);
    fixture.detectChanges();

    component.setFilter('all');
    http.expectOne('/api/admin/settings').flush(settings);
    expectAbsences(http, 'all').flush([]);
    component.setYear('2027');
    http.expectOne('/api/admin/settings').flush(settings);
    expectAbsences(http, 'all', '2027').flush([]);
    http.verify();
    fixture.destroy();
  });

  it('points to the settings when the absence targets are missing', async () => {
    const { fixture, http, page } = await setup();

    http.expectOne('/api/admin/settings').flush({ ...settings, sickActivityId: null });
    expectAbsences(http, 'open').flush([]);
    fixture.detectChanges();

    const hint = page.querySelector('.hint.warning');
    expect(hint?.textContent).toContain('noch nicht eingerichtet');
    expect(hint?.querySelector('a')?.getAttribute('href')).toBe('/admin#absences');
    fixture.destroy();
  });

  describe('create for an employee', () => {
    async function openCreate(all: AdminAbsence[] = []) {
      const context = await setup();
      flushRequests(context.http, []);
      context.fixture.detectChanges();
      button(context.page, 'Erfassen').click();
      context.http.expectOne('/api/admin/settings').flush(settings);
      expectAbsences(context.http, 'all').flush(all);
      context.fixture.detectChanges();
      return context;
    }

    function dayButton(page: HTMLElement, number: number): HTMLButtonElement {
      return Array.from(page.querySelectorAll<HTMLButtonElement>('app-day-picker-calendar button.day'))
        .find(item => item.textContent?.trim() === String(number))!;
    }

    it('offers only active employees with a Kimai token', async () => {
      const { fixture, page } = await openCreate();

      const options = Array.from(page.querySelectorAll('.create select option'), option => option.textContent?.trim());
      expect(options).toEqual(['Anna', 'Max']);
      fixture.destroy();
    });

    it('selects days by tapping, blocks taken days and shows the remainder before and after', async () => {
      const taken = absence({ id: 'x', status: 'applied', kind: 'sick', days: ['2026-10-21'] });
      const foreign = absence({ id: 'y', employeeId: 'max', employeeName: 'Max', days: ['2026-10-22'] });
      const { fixture, http, page, component } = await openCreate([taken, foreign, absence({ id: 'z', status: 'rejected', days: ['2026-10-23'] })]);

      expect(dayButton(page, 21).disabled).toBe(true);
      expect(dayButton(page, 21).title).toBe('Krank (In Kimai eingetragen)');
      expect(dayButton(page, 22).disabled).toBe(false);
      expect(dayButton(page, 23).disabled).toBe(false);

      dayButton(page, 19).click();
      dayButton(page, 20).click();
      component.createComment.set('Telefonisch gemeldet');
      fixture.detectChanges();
      expect(page.textContent).toContain('Mo 19.–Di 20.10.');

      button(page, 'Weiter').click();
      expectBalances(http, 2026).flush([balance('anna', 'Anna', 1)]);
      fixture.detectChanges();

      const text = page.textContent ?? '';
      expect(text).toContain('Zusammenfassung');
      expect(text).toContain('vorher 1 Tag, nachher -1 Tage');
      expect(text).toContain('übersteigt den Rest');

      button(page, 'Jetzt erfassen').click();
      const create = http.expectOne(candidate => candidate.url === '/api/admin/absences' && candidate.method === 'POST');
      expect(create.request.body).toEqual({
        employeeId: 'anna', kind: 'vacation', days: ['2026-10-19', '2026-10-20'], comment: 'Telefonisch gemeldet',
      });
      create.flush(absence({
        status: 'applied', days: ['2026-10-19', '2026-10-20'], source: 'admin', createdBy: 'Admin',
        warning: 'Urlaub übersteigt den Rest 2026 um 1 Tage.',
      }));
      expectAbsences(http, 'all').flush([]);
      fixture.detectChanges();

      expect(page.textContent).toContain('In Kimai eingetragen');
      expect(page.textContent).toContain('Urlaub übersteigt den Rest 2026 um 1 Tage.');
      http.verify();
      fixture.destroy();
    });

    it('shows the reason when the entry failed', async () => {
      const { fixture, http, page, component } = await openCreate();
      component.selectKind('flexDay');
      component.createDays.set(['2026-10-19']);
      fixture.detectChanges();

      button(page, 'Weiter').click();
      fixture.detectChanges();
      button(page, 'Jetzt erfassen').click();
      http.expectOne(candidate => candidate.url === '/api/admin/absences' && candidate.method === 'POST')
        .flush(absence({ kind: 'flexDay', status: 'failed', days: ['2026-10-19'], error: 'Am 19.10.2026 ist kein Platz mehr' }));
      expectAbsences(http, 'all').flush([]);
      fixture.detectChanges();

      expect(page.textContent).toContain('Nicht eingetragen');
      expect(page.textContent).toContain('kein Platz mehr');
      fixture.destroy();
    });

    it('keeps the result but ends the session when the refresh after creating is refused', async () => {
      const { fixture, http, page, component } = await openCreate();
      component.selectKind('sick');
      component.createDays.set(['2026-10-19']);
      component.showSummary();
      fixture.detectChanges();

      button(page, 'Jetzt erfassen').click();
      http.expectOne(candidate => candidate.url === '/api/admin/absences' && candidate.method === 'POST')
        .flush(absence({ kind: 'sick', status: 'applied', days: ['2026-10-19'] }));
      expectAbsences(http, 'all').flush({}, { status: 401, statusText: 'Unauthorized' });
      fixture.detectChanges();

      expect(page.textContent).toContain('In Kimai eingetragen');
      expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
      expect(component.message()).toContain('bitte neu anmelden');
      fixture.destroy();
    });

    it('drops days outside the window of a newly chosen kind', async () => {
      const { fixture, component } = await openCreate();
      component.createDays.set(['2026-10-20', '2026-12-01']);

      component.selectKind('sick');

      expect(component.createDays()).toEqual(['2026-10-20']);
      fixture.destroy();
    });
  });

  describe('vacation accounts', () => {
    async function openBalances() {
      const context = await setup();
      flushRequests(context.http, []);
      context.fixture.detectChanges();
      button(context.page, 'Urlaubskonten').click();
      context.http.expectOne('/api/admin/settings').flush(settings);
      expectBalances(context.http, 2026).flush([
        balance('anna', 'Anna', 10, { personnelNumber: '1007', fullMonths: 9, balance: { year: 2026, entitlement: 23, carryOver: 2, correction: -1, taken: 8, planned: 6, remaining: 10 } }),
        balance('max', 'Max', 30, { vacationDaysPerYear: null, entryDate: null }),
      ]);
      expectBalances(context.http, 2025).flush([balance('anna', 'Anna', 3.5), balance('max', 'Max', 0)]);
      context.fixture.detectChanges();
      return context;
    }

    it('shows the values of the server and missing master data', async () => {
      const { fixture, page } = await openBalances();

      const anna = page.querySelectorAll<HTMLElement>('.balance-row')[0];
      expect(anna.textContent).toContain('Pers.-Nr. 1007');
      expect(anna.textContent).toContain('23 Tage');
      expect(anna.querySelector('strong[title]')?.getAttribute('title')).toBe('9 volle Monate');
      expect(Array.from(anna.querySelectorAll<HTMLInputElement>('input'), input => input.value)).toEqual(['2', '-1', '']);

      const max = page.querySelectorAll<HTMLElement>('.balance-row')[1];
      expect(max.querySelector('.hint')?.textContent).toContain('Urlaubstage pro Jahr fehlen, Eintritt fehlt');
      expect(max.querySelector('.hint a')?.getAttribute('href')).toBe('/admin#employees');
      fixture.destroy();
    });

    it('saves carry-over and correction per row, with the previous remainder as a suggestion', async () => {
      const { fixture, http, page, component } = await openBalances();
      const anna = component.balances()[0];

      button(page, 'Rest aus 2025 übernehmen (3,5 Tage)').click();
      component.updateDraft(anna, { correction: '1,5', note: ' Vorjahr ' });
      fixture.detectChanges();
      page.querySelector<HTMLElement>('.balance-row')!.querySelector<HTMLButtonElement>('button.primary')!.click();

      const save = http.expectOne('/api/admin/absences/balances/anna/2026');
      expect(save.request.method).toBe('PUT');
      expect(save.request.body).toEqual({ carryOverDays: 3.5, correctionDays: 1.5, note: 'Vorjahr' });
      save.flush(balance('anna', 'Anna', 14, { note: 'Vorjahr', balance: { year: 2026, entitlement: 23, carryOver: 3.5, correction: 1.5, taken: 8, planned: 6, remaining: 14 } }));
      fixture.detectChanges();

      expect(component.message()).toBe('Anna: Urlaubskonto 2026 gespeichert.');
      expect(component.isDirty(component.balances()[0])).toBe(false);
      expect(page.querySelector('.balance-row')?.textContent).toContain('14 Tage');
      // No suggestion without a positive remainder.
      expect(page.querySelectorAll('.balance-row')[1].textContent).not.toContain('übernehmen');
      fixture.destroy();
    });

    it('does not save numbers it cannot read', async () => {
      const { fixture, http, page, component } = await openBalances();

      component.updateDraft(component.balances()[0], { carryOver: 'zwei' });
      fixture.detectChanges();

      expect(page.querySelector<HTMLElement>('.balance-row')!.querySelector<HTMLButtonElement>('button.primary')!.disabled).toBe(true);
      http.expectNone('/api/admin/absences/balances/anna/2026');
      fixture.destroy();
    });
  });

  it('formats day lists compactly', () => {
    expect(compactDays(['2026-11-10', '2026-11-02', '2026-11-03', '2026-11-04'], 2026)).toBe('Mo 02.–Mi 04.11., Di 10.11.');
    expect(compactDays(['2026-10-30', '2026-10-31', '2026-11-01', '2026-11-02'], 2026)).toBe('Fr 30.10.–Mo 02.11.');
    expect(compactDays(['2026-12-31', '2027-01-01'], 2026)).toBe('Do 31.12.2026–Fr 01.01.2027');
    expect(compactDays(['2027-01-04'], 2026)).toBe('Mo 04.01.2027');
    expect(compactDays(['2027-01-04', '2027-01-05'], 2026)).toBe('Mo 04.–Di 05.01.2027');
  });

  it('uses the admin window per kind', () => {
    expect(adminWindow('vacation', '2026-10-15')).toEqual({ min: '2026-07-17', max: '2028-04-15' });
    expect(adminWindow('sick', '2026-10-15')).toEqual({ min: '2026-07-17', max: '2026-10-29' });
    expect(adminWindow('flexDay', '2026-10-15')).toEqual({ min: '2026-07-17', max: '2027-10-15' });
  });
});
