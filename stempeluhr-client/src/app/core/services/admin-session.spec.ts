import { TestBed } from '@angular/core/testing';

import { AdminSession } from './admin-session';

describe('AdminSession', () => {
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    sessionStorage.clear();
  });

  it('merkt sich das Passwort für den Tab und vergisst es beim Abmelden', () => {
    const session = TestBed.inject(AdminSession);
    expect(session.isLoggedIn()).toBe(false);

    session.remember('geheim');
    expect(session.password()).toBe('geheim');
    expect(session.isLoggedIn()).toBe(true);
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBe('geheim');

    session.clear();
    expect(session.password()).toBe('');
    expect(session.isLoggedIn()).toBe(false);
    expect(sessionStorage.getItem(AdminSession.StorageKey)).toBeNull();
  });

  it('übernimmt eine bestehende Anmeldung nach dem Neuladen', () => {
    sessionStorage.setItem(AdminSession.StorageKey, 'geheim');

    expect(TestBed.inject(AdminSession).password()).toBe('geheim');
  });

  it('bleibt ohne nutzbaren Speicher im Speicher angemeldet', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('blocked'); });
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => { throw new DOMException('blocked'); });
    const session = TestBed.inject(AdminSession);

    session.remember('geheim');
    expect(session.password()).toBe('geheim');
    session.clear();
    expect(session.isLoggedIn()).toBe(false);
  });
});
