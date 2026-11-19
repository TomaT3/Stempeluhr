import { ComponentFixture, TestBed } from '@angular/core/testing';

import { OnscreenKeyboard } from './onscreen-keyboard';

describe('OnscreenKeyboard', () => {
  let fixture: ComponentFixture<OnscreenKeyboard>;
  let keyboard: OnscreenKeyboard;

  beforeEach(() => {
    fixture = TestBed.createComponent(OnscreenKeyboard);
    keyboard = fixture.componentInstance;
    fixture.detectChanges();
  });

  function tap(label: string): void {
    const key = [...fixture.nativeElement.querySelectorAll('button')]
      .find((button: HTMLButtonElement) => (button.getAttribute('aria-label') ?? button.textContent?.trim()) === label);
    if (!key) throw new Error(`no key ${label}`);
    (key as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  it('types into the value, shift applies to one letter', () => {
    tap('Großbuchstaben');
    tap('A');
    tap('b');
    tap('Leerzeichen');
    tap('1');

    expect(keyboard.value()).toBe('Ab 1');
  });

  it('switches to symbols and back, deletes the last character', () => {
    tap('?123');
    tap('@');
    tap('\\');
    tap('abc');
    tap('x');
    tap('Löschen');

    expect(keyboard.value()).toBe('@\\');
  });

  it('only offers printable ASCII', () => {
    for (const layer of ['letters', 'symbols'] as const) {
      keyboard.layer.set(layer);
      fixture.detectChanges();
      const keys = [...fixture.nativeElement.querySelectorAll('.row:not(:last-child) .key')]
        .map(key => (key as HTMLElement).textContent?.trim() ?? '');
      expect(keys.every(key => /^[!-~]$/.test(key))).toBe(true);
    }
  });

  it('stops at the maximum length', () => {
    fixture.componentRef.setInput('maxLength', 2);
    tap('a');
    tap('s');
    tap('d');

    expect(keyboard.value()).toBe('as');
  });
});
