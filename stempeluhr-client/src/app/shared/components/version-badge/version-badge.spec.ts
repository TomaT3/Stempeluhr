import { computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { APP_VERSION } from '../../../core/app-version';
import { AppVersionService } from '../../../core/services/app-version.service';
import { VersionBadge } from './version-badge';

describe('VersionBadge', () => {
  let fixture: ComponentFixture<VersionBadge>;
  let serverVersion: ReturnType<typeof signal<string | null>>;

  beforeEach(() => {
    serverVersion = signal<string | null>(null);
    TestBed.configureTestingModule({
      imports: [VersionBadge],
      providers: [
        {
          provide: AppVersionService,
          useValue: {
            serverVersion,
            versionMismatch: computed(
              () => serverVersion() !== null && serverVersion() !== APP_VERSION,
            ),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(VersionBadge);
  });

  it('zeigt Client- und Server-Version an', () => {
    serverVersion.set('0.6.3');
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain(`Client v${APP_VERSION}`);
    expect(text).toContain('Server v0.6.3');
  });

  it('markiert einen Versions-Mismatch (alte App aus dem Browser-Cache)', () => {
    serverVersion.set('9.9.9');
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).toContain('version-mismatch');
  });

  it('zeigt keinen Mismatch, wenn Client- und Server-Version identisch sind', () => {
    serverVersion.set(APP_VERSION);
    fixture.detectChanges();

    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).not.toContain('version-mismatch');
  });

  it('zeigt einen Platzhalter, wenn keine Server-Version bekannt ist', () => {
    fixture.detectChanges();

    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Server v–');
    const badge = fixture.nativeElement.querySelector('.version-badge') as HTMLElement;
    expect(badge.classList).not.toContain('version-mismatch');
  });
});
