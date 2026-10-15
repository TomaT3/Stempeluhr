import { TestBed } from '@angular/core/testing';

import { addDays, addMonths, DayPickerCalendar, localToday } from './day-picker-calendar';

describe('DayPickerCalendar', () => {
  async function setup(inputs: Record<string, unknown> = {}) {
    await TestBed.configureTestingModule({ imports: [DayPickerCalendar] }).compileComponents();
    const fixture = TestBed.createComponent(DayPickerCalendar);
    fixture.componentRef.setInput('today', '2026-10-15');
    for (const [name, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(name, value);
    }
    fixture.detectChanges();
    return { fixture, component: fixture.componentInstance, page: fixture.nativeElement as HTMLElement };
  }

  function day(page: HTMLElement, number: number): HTMLButtonElement {
    const found = Array.from(page.querySelectorAll<HTMLButtonElement>('button.day'))
      .find(button => button.textContent?.trim() === String(number));
    expect(found).toBeDefined();
    return found!;
  }

  function navButton(page: HTMLElement, label: string): HTMLButtonElement {
    return page.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`)!;
  }

  it('starts in the month of today, with Monday first', async () => {
    const { page } = await setup();

    expect(page.querySelector('strong')?.textContent).toBe('Oktober 2026');
    // 1 October 2026 is a Thursday: three empty cells before it.
    expect(page.querySelectorAll('.grid .pad').length).toBe(3);
    expect(page.querySelectorAll('button.day').length).toBe(31);
    expect(day(page, 15).classList).toContain('today');
    expect(day(page, 5).getAttribute('aria-label')).toBe('Mo 05.10.2026');
  });

  it('selects several days by tapping and deselects them again', async () => {
    const { fixture, component, page } = await setup();

    day(page, 20).click();
    day(page, 12).click();
    day(page, 13).click();
    fixture.detectChanges();
    expect(component.selected()).toEqual(['2026-10-12', '2026-10-13', '2026-10-20']);
    expect(day(page, 12).getAttribute('aria-pressed')).toBe('true');

    day(page, 13).click();
    fixture.detectChanges();
    expect(component.selected()).toEqual(['2026-10-12', '2026-10-20']);
    expect(day(page, 13).classList).not.toContain('selected');
  });

  it('switches months and keeps the selection', async () => {
    const { fixture, component, page } = await setup();
    day(page, 30).click();

    navButton(page, 'Nächster Monat').click();
    fixture.detectChanges();
    expect(page.querySelector('strong')?.textContent).toBe('November 2026');
    day(page, 2).click();
    navButton(page, 'Vorheriger Monat').click();
    navButton(page, 'Vorheriger Monat').click();
    fixture.detectChanges();

    expect(page.querySelector('strong')?.textContent).toBe('September 2026');
    expect(component.selected()).toEqual(['2026-10-30', '2026-11-02']);
  });

  it('crosses the year boundary', async () => {
    const { fixture, page } = await setup({ today: '2026-12-31' });

    navButton(page, 'Nächster Monat').click();
    fixture.detectChanges();

    expect(page.querySelector('strong')?.textContent).toBe('Januar 2027');
  });

  it('does not select blocked days and shows what blocks them', async () => {
    const { fixture, component, page } = await setup({
      blocked: { '2026-10-12': { label: 'Urlaub (offen)', tone: 'vacation' } },
    });

    const blocked = day(page, 12);
    expect(blocked.disabled).toBe(true);
    expect(blocked.classList).toContain('blocked');
    expect(blocked.dataset['tone']).toBe('vacation');
    expect(blocked.title).toBe('Urlaub (offen)');
    expect(blocked.getAttribute('aria-label')).toBe('Mo 12.10.2026, Urlaub (offen)');

    component.toggle(component.cells().find(cell => cell?.day === '2026-10-12')!);
    fixture.detectChanges();
    expect(component.selected()).toEqual([]);
  });

  it('limits the selectable range and the month navigation to min and max', async () => {
    const { fixture, page } = await setup({ min: '2026-10-10', max: '2026-11-05' });

    expect(day(page, 9).disabled).toBe(true);
    expect(day(page, 10).disabled).toBe(false);
    expect(navButton(page, 'Vorheriger Monat').disabled).toBe(true);

    navButton(page, 'Nächster Monat').click();
    fixture.detectChanges();
    expect(day(page, 5).disabled).toBe(false);
    expect(day(page, 6).disabled).toBe(true);
    expect(navButton(page, 'Nächster Monat').disabled).toBe(true);
  });

  it('can be disabled as a whole', async () => {
    const { component, page } = await setup({ disabled: true });

    day(page, 20).click();

    expect(day(page, 20).disabled).toBe(true);
    expect(component.selected()).toEqual([]);
  });

  it('calculates dates without time zone effects', () => {
    expect(addDays('2026-10-31', 1)).toBe('2026-11-01');
    expect(addDays('2027-03-28', 1)).toBe('2027-03-29');
    expect(addMonths('2026-01-31', 1)).toBe('2026-02-28');
    expect(addMonths('2026-10-15', 18)).toBe('2028-04-15');
    expect(localToday(new Date(2026, 9, 5, 23, 59))).toBe('2026-10-05');
  });
});
