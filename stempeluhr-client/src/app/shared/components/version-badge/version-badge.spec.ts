import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { APP_VERSION } from '../../../core/app-version';
import { VersionBadge } from './version-badge';

describe('VersionBadge', () => {
  let fixture: ComponentFixture<VersionBadge>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [VersionBadge],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(VersionBadge);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function flushHealth(version: string | null): void {
    httpMock
      .expectOne('/api/health')
      .flush({ ok: true, version, configuredEmployees: 0, settingsConfigured: true });
  }

  it('zeigt Client- und Server-Version an', () => {
    fixture.detectChanges();
    flushHealth('0.6.3');
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain(`Client v${APP_VERSION}`);
    expect(text).toContain('Server v0.6.3');
  });

  it('markiert einen Versions-Mismatch (alte App aus dem Browser-Cache)', () => {
    fixture.detectChanges();
    flushHealth('9.9.9');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).toContain('version-mismatch');
  });

  it('zeigt keinen Mismatch, wenn Client- und Server-Version identisch sind', () => {
    fixture.detectChanges();
    flushHealth(APP_VERSION);
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).not.toContain('version-mismatch');
  });

  it('zeigt einen Platzhalter, wenn der Server nicht erreichbar ist', () => {
    fixture.detectChanges();
    httpMock.expectOne('/api/health').error(new ErrorEvent('Network error'));
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Server v–');
    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).not.toContain('version-mismatch');
  });

  it('aktualisiert die Server-Version periodisch (Kiosk läuft tagelang)', () => {
    vi.useFakeTimers();
    try {
      fixture.detectChanges();
      httpMock
        .expectOne('/api/health')
        .flush({ ok: true, version: '0.6.3', configuredEmployees: 0, settingsConfigured: true });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.6.3');

      // Nächster Poll nach 60 s: Server wurde inzwischen aktualisiert.
      vi.advanceTimersByTime(60_000);
      httpMock
        .expectOne('/api/health')
        .flush({ ok: true, version: '0.7.0', configuredEmployees: 0, settingsConfigured: true });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.7.0');
    } finally {
      vi.useRealTimers();
    }
  });

  it('behält die zuletzt bekannte Server-Version, wenn ein Poll fehlschlägt', () => {
    vi.useFakeTimers();
    try {
      fixture.detectChanges();
      httpMock
        .expectOne('/api/health')
        .flush({ ok: true, version: '0.6.3', configuredEmployees: 0, settingsConfigured: true });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.6.3');

      vi.advanceTimersByTime(60_000);
      httpMock.expectOne('/api/health').error(new ErrorEvent('Network error'));
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.6.3');
    } finally {
      vi.useRealTimers();
    }
  });

  it('behält die Server-Version bei einem hängenden Request (Timeout nach 10 s)', () => {
    vi.useFakeTimers();
    try {
      fixture.detectChanges();
      httpMock
        .expectOne('/api/health')
        .flush({ ok: true, version: '0.6.3', configuredEmployees: 0, settingsConfigured: true });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.6.3');

      // Nächster Poll: Server akzeptiert TCP, antwortet aber nie.
      vi.advanceTimersByTime(60_000);
      httpMock.expectOne('/api/health');

      // Nach 10 s greift der timeout()-Operator -> Fehler -> letzte Version bleibt.
      // Der Operator cancelt den Request; verify() im afterEach ignoriert
      // gecancelte Requests, daher ist kein manuelles Aufräumen nötig.
      vi.advanceTimersByTime(10_000);
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain('Server v0.6.3');
    } finally {
      vi.useRealTimers();
    }
  });
});
