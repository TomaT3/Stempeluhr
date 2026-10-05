import { ComponentFixture, TestBed } from '@angular/core/testing';

import { TimeStepper } from './time-stepper';

describe('TimeStepper', () => {
  let fixture: ComponentFixture<TimeStepper>;

  function create(value: string, bounds: { min?: string; max?: string; disabled?: boolean } = {}): void {
    fixture = TestBed.createComponent(TimeStepper);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('min', bounds.min ?? null);
    fixture.componentRef.setInput('max', bounds.max ?? null);
    fixture.componentRef.setInput('disabled', bounds.disabled ?? false);
    fixture.detectChanges();
  }

  function press(selector: string): void {
    (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function shown(): string[] {
    return [...fixture.nativeElement.querySelectorAll('.value')].map(element => (element as HTMLElement).textContent?.trim() ?? '');
  }

  it('shows day, hour and minute', () => {
    create('2026-10-05T08:07');

    expect(shown()).toEqual(['Mo 05.10.', '08', '07']);
  });

  it('steps day, hour and minutes with large buttons', () => {
    create('2026-10-05T08:07');

    press('.day-earlier');
    press('.hour-later');
    press('.minute-later-5');
    press('.minute-earlier-1');

    expect(fixture.componentInstance.value()).toBe('2026-10-04T09:11');
    expect(shown()).toEqual(['So 04.10.', '09', '11']);
  });

  it('rolls minutes over into the next hour and day', () => {
    create('2026-10-05T23:58');

    press('.minute-later-5');

    expect(fixture.componentInstance.value()).toBe('2026-10-06T00:03');
  });

  it('disables the buttons that would leave the allowed range', () => {
    create('2026-10-05T08:00', { min: '2026-10-05T07:58', max: '2026-10-05T08:30' });

    const disabled = (selector: string) => (fixture.nativeElement.querySelector(selector) as HTMLButtonElement).disabled;
    expect(disabled('.minute-earlier-5')).toBe(true);
    expect(disabled('.minute-earlier-1')).toBe(false);
    expect(disabled('.hour-later')).toBe(true);
    expect(disabled('.minute-later-5')).toBe(false);

    press('.minute-earlier-5');
    expect(fixture.componentInstance.value()).toBe('2026-10-05T08:00');
  });

  it('locks everything while disabled', () => {
    create('2026-10-05T08:00', { disabled: true });

    const buttons = [...fixture.nativeElement.querySelectorAll('button')] as HTMLButtonElement[];
    expect(buttons.length).toBe(8);
    expect(buttons.every(button => button.disabled)).toBe(true);
  });

  it('labels the buttons for assistive technology', () => {
    create('2026-10-05T08:00');

    const labels = [...fixture.nativeElement.querySelectorAll('button')].map(button => button.getAttribute('aria-label'));
    expect(labels).toContain('Einen Tag früher');
    expect(labels).toContain('Fünf Minuten später');
  });
});
