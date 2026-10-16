import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { AdminSession } from '../../../core/services/admin-session';
import { MonthlyExportSection } from './monthly-export-section';

describe('MonthlyExportSection', () => {
  const exportUrl = (request: { url: string }) => request.url === '/api/admin/export/month';

  beforeEach(() => sessionStorage.setItem(AdminSession.StorageKey, 'test-password'));
  afterEach(() => {
    sessionStorage.clear();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  async function setup() {
    await TestBed.configureTestingModule({
      imports: [MonthlyExportSection],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    const fixture = TestBed.createComponent(MonthlyExportSection);
    fixture.detectChanges();
    const page = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      page,
      http: TestBed.inject(HttpTestingController),
      button: () => page.querySelector<HTMLButtonElement>('button')!,
      monthInput: () => page.querySelector<HTMLInputElement>('#export-month')!,
    };
  }

  it('preselects the previous month', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 0, 15, 12));
    const { fixture, monthInput } = await setup();

    expect(fixture.componentInstance.month()).toBe('2025-12');
    expect(monthInput().value).toBe('2025-12');
    fixture.destroy();
  });

  it('downloads the workbook as a blob with the admin header and shows the loading state', async () => {
    const createObjectURL = vi.fn(() => 'blob:export');
    const revokeObjectURL = vi.fn();
    Object.assign(URL, { createObjectURL, revokeObjectURL });
    const downloads: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      downloads.push(this.download);
    });
    const { fixture, http, page, button, monthInput } = await setup();

    monthInput().value = '2026-09';
    monthInput().dispatchEvent(new Event('input'));
    button().click();
    fixture.detectChanges();

    const request = http.expectOne(exportUrl);
    expect(request.request.headers.get('X-Admin-Password')).toBe('test-password');
    expect(request.request.params.get('year')).toBe('2026');
    expect(request.request.params.get('month')).toBe('9');
    expect(request.request.responseType).toBe('blob');
    expect(button().disabled).toBe(true);
    expect(button().textContent).toContain('Export wird erstellt');
    expect(page.querySelector('.loading-note')).not.toBeNull();

    const file = new Blob(['xlsx'], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' });
    request.flush(file);
    fixture.detectChanges();

    expect(createObjectURL).toHaveBeenCalledWith(file);
    expect(downloads).toEqual(['Stempeluhr-Export-2026-09.xlsx']);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:export');
    expect(button().disabled).toBe(false);
    expect(page.querySelector('.message')).toBeNull();
    fixture.destroy();
  });

  it('shows an error and allows another try when the export fails', async () => {
    const { fixture, http, page, button } = await setup();

    button().click();
    http.expectOne(exportUrl).flush(new Blob(['boom']), { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(page.querySelector('.message')?.textContent).toContain('Der Export konnte nicht erstellt werden (500).');
    expect(button().disabled).toBe(false);
    fixture.destroy();
  });

  it('ends the session and reports it when the password is refused', async () => {
    const { fixture, http, page, button } = await setup();
    const rejected = vi.fn();
    fixture.componentInstance.sessionRejected.subscribe(rejected);

    button().click();
    http.expectOne(exportUrl).flush(new Blob([]), { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
    expect(rejected).toHaveBeenCalledOnce();
    expect(page.querySelector('.message')?.textContent).toContain('Admin-Passwort ungültig.');
    fixture.destroy();
  });

  it('does not request anything without a month', async () => {
    const { fixture, http, page, button, monthInput } = await setup();

    monthInput().value = '';
    monthInput().dispatchEvent(new Event('input'));
    button().click();
    fixture.detectChanges();

    http.expectNone(exportUrl);
    expect(page.querySelector('.message')?.textContent).toContain('Bitte einen Monat wählen.');
    fixture.destroy();
  });
});
