import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { Subject } from 'rxjs';

import { KioskEmployeeSession } from '../../../core/models/kiosk.models';
import { AudioFeedback } from '../../../core/services/audio-feedback';
import { KioskApi } from '../../../core/services/kiosk-api';
import { ClockPage } from './clock-page';

describe('ClockPage', () => {
  let pinLogin: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    pinLogin = vi.fn(() => new Subject<KioskEmployeeSession>());

    await TestBed.configureTestingModule({
      imports: [ClockPage],
      providers: [
        { provide: KioskApi, useValue: { pinLogin } },
        { provide: AudioFeedback, useValue: { playBeeps: vi.fn() } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: { get: () => null } } },
        },
      ],
    }).compileComponents();
  });

  it('confirms the pin automatically after the fourth digit', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');

    expect(pinLogin).not.toHaveBeenCalled();

    component.pressDigit('4');

    expect(component.pin()).toBe('1234');
    expect(pinLogin).toHaveBeenCalledExactlyOnceWith('1234');
    expect(component.isBusy()).toBe(true);
  });

  it('ignores further digits while the pin login is in progress', () => {
    const fixture = TestBed.createComponent(ClockPage);
    const component = fixture.componentInstance;

    component.pressDigit('1');
    component.pressDigit('2');
    component.pressDigit('3');
    component.pressDigit('4');
    component.pressDigit('5');

    expect(component.pin()).toBe('1234');
    expect(pinLogin).toHaveBeenCalledOnce();
  });
});
