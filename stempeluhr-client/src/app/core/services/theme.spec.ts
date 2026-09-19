import { TestBed } from '@angular/core/testing';

import { ThemeService } from './theme';

describe('ThemeService', () => {
  let service: ThemeService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ThemeService);
    delete document.documentElement.dataset['theme'];
  });

  afterEach(() => {
    delete document.documentElement.dataset['theme'];
  });

  it('setzt data-theme auf <html> und merkt sich das Theme', () => {
    service.apply('dark');

    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(service.theme()).toBe('dark');
  });

  it('nimmt das Attribut beim Zurückschalten wieder auf hell', () => {
    service.apply('dark');
    service.apply('light');

    expect(document.documentElement.dataset['theme']).toBe('light');
    expect(service.theme()).toBe('light');
  });
});
