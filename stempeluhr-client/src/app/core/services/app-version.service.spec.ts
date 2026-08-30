import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';

import { AppVersionService } from './app-version.service';

describe('AppVersionService', () => {
  let service: AppVersionService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    // service wird bewusst NICHT hier erstellt: Fake-Timer-Tests müssen den
    // Poll-Stream mit fake Timers aufbauen (sonst läuft das 60s-Intervall echt).
  });

  afterEach(() => {
    httpMock.verify();
  });

  function flushHealth(version: string | null): void {
    httpMock
      .expectOne('/api/health')
      .flush({ ok: true, version, configuredEmployees: 0, settingsConfigured: true });
  }

  it('pollt die Server-Version sofort (startWith) und setzt Mismatch', () => {
    service = TestBed.inject(AppVersionService);
    flushHealth('0.6.3');
    expect(service.serverVersion()).toBe('0.6.3');
    expect(service.versionMismatch()).toBe(true);
  });

  it('zeigt keinen Mismatch bei identischer Version', () => {
    service = TestBed.inject(AppVersionService);
    flushHealth('0.0.0-local');
    expect(service.versionMismatch()).toBe(false);
  });

  it('pollt periodisch weiter (60 s) und aktualisiert die Version', () => {
    vi.useFakeTimers();
    try {
      service = TestBed.inject(AppVersionService);
      flushHealth('0.6.3');
      expect(service.serverVersion()).toBe('0.6.3');

      vi.advanceTimersByTime(60_000);
      flushHealth('0.7.0');
      expect(service.serverVersion()).toBe('0.7.0');
    } finally {
      vi.useRealTimers();
    }
  });

  it('behält die letzte Version bei Fehlern und emittiert version$ nur bei Änderung', () => {
    vi.useFakeTimers();
    try {
      service = TestBed.inject(AppVersionService);
      const changes: (string | null)[] = [];
      service.version$.subscribe(v => changes.push(v));
      expect(changes).toEqual([null]); // BehaviorSubject-Initialwert

      flushHealth('0.6.3');
      expect(changes).toEqual([null, '0.6.3']);

      vi.advanceTimersByTime(60_000);
      httpMock.expectOne('/api/health').error(new ErrorEvent('Network error'));
      expect(service.serverVersion()).toBe('0.6.3');
      expect(changes).toEqual([null, '0.6.3']); // keine neue Emission
    } finally {
      vi.useRealTimers();
    }
  });

  it('behält die letzte Version bei einem hängenden Request (Timeout nach 10 s)', () => {
    vi.useFakeTimers();
    try {
      service = TestBed.inject(AppVersionService);
      flushHealth('0.6.3');

      vi.advanceTimersByTime(60_000);
      httpMock.expectOne('/api/health'); // hängt -> Timeout nach 10 s

      vi.advanceTimersByTime(10_000);
      expect(service.serverVersion()).toBe('0.6.3');
    } finally {
      vi.useRealTimers();
    }
  });
});
