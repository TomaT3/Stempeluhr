import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';

import { LocalNfcScan, LOCAL_NFC_SCAN_PORT, LocalNfcScanService } from './local-nfc-scan.service';

describe('LocalNfcScanService', () => {
  let service: LocalNfcScanService;
  let httpMock: HttpTestingController;

  const scan = (cardId: string, scannedAt: string, consumed = false): LocalNfcScan => ({
    cardId,
    scannedAt,
    consumed,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: LOCAL_NFC_SCAN_PORT, useValue: 8737 }],
    });

    service = TestBed.inject(LocalNfcScanService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('polls the agent loopback endpoint on the configured port', () => {
    let emitted: LocalNfcScan | null = null;
    service.poll().subscribe(result => (emitted = result));

    const req = httpMock.expectOne('http://127.0.0.1:8737/scan/latest');
    expect(req.request.method).toBe('GET');
    req.flush(scan('04AB1122', '2026-08-25T08:00:00Z'));

    expect(emitted).not.toBeNull();
    expect(emitted!.cardId).toBe('04AB1122');
  });

  it('emits null when the agent is unreachable instead of erroring', () => {
    let emitted: LocalNfcScan | null | undefined;
    let errored = false;
    service.poll().subscribe({ next: result => (emitted = result), error: () => (errored = true) });

    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(null, { status: 0, statusText: 'Unknown Error' });

    expect(errored).toBeFalsy();
    expect(emitted).toBeNull();
  });

  it('emits each new scan once and suppresses repeats of the same scan', () => {
    const emissions: Array<LocalNfcScan | null> = [];

    service.poll().subscribe(result => emissions.push(result));
    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('04AB', '2026-08-25T08:00:00Z'));

    service.poll().subscribe(result => emissions.push(result));
    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('04AB', '2026-08-25T08:00:00Z'));

    service.poll().subscribe(result => emissions.push(result));
    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('04AB', '2026-08-25T08:01:00Z'));

    expect(emissions[0]!.cardId).toBe('04AB');
    expect(emissions[1]).toBeNull();
    // Fresher scannedAt counts as a new scan even for the same card.
    expect(emissions[2]!.scannedAt).toBe('2026-08-25T08:01:00Z');
  });

  it('treats a card change as a new scan even with an older timestamp', () => {
    service.poll().subscribe();

    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('04AB', '2026-08-25T08:00:00Z'));

    const second: Array<LocalNfcScan | null> = [];
    service.poll().subscribe(result => second.push(result));
    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('FF01', '2026-08-25T07:59:00Z'));

    expect(second.length).toBe(1);
    expect(second[0]!.cardId).toBe('FF01');
  });

  it('ignores consumed scans entirely', () => {
    let emitted: LocalNfcScan | null | undefined;
    service.poll().subscribe(result => (emitted = result));

    httpMock.expectOne('http://127.0.0.1:8737/scan/latest').flush(scan('04AB', '2026-08-25T08:00:00Z', true));

    expect(emitted).toBeNull();
  });

  it('acks via POST to /scan/ack', () => {
    service.ack().subscribe();

    const req = httpMock.expectOne('http://127.0.0.1:8737/scan/ack');
    expect(req.request.method).toBe('POST');
    req.flush(null);
  });
});
