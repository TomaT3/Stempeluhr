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
});
