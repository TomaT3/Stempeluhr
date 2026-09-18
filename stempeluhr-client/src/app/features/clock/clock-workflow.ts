import { Directive, OnDestroy, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Subscription } from 'rxjs';

import { APP_VERSION, DEV_VERSION } from '../../core/app-version';
import { ClockStatus, Employee, HoursOverview, NfcClockEvent } from '../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../core/models/offline.models';
import { AppVersionService } from '../../core/services/app-version.service';
import { AudioFeedback } from '../../core/services/audio-feedback';
import { ClockState, projectClockStatus } from '../../core/services/clock-state';
import { KioskApi } from '../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../core/services/local-nfc-scan.service';
import {
  forgetEmployeePin,
  lastKnownStatus,
  normalizeCardId,
  rememberEmployeeCard,
  rememberEmployeePin,
  rememberObservedStatus,
  rememberProjectedStatus,
  resolveEmployeeByCard,
  resolveEmployeeByPin,
  toOfflineStatus,
  withOfflineLabel,
} from '../../core/services/offline-cache';
import { OfflineQueueService } from '../../core/services/offline-queue';

/** Wartezeit zwischen Versions-Hinweis und Auto-Reload (Mitarbeiter kann abbrechen). */
const VERSION_RELOAD_DELAY_MS = 3000;

const PIN_LENGTH = 4;

@Directive()
export abstract class ClockWorkflow implements OnDestroy {
  private readonly kioskApi = inject(KioskApi);
  private readonly audioFeedback = inject(AudioFeedback);
  private readonly route = inject(ActivatedRoute);
  private readonly localNfcScan = inject(LocalNfcScanService);
  private readonly appVersion = inject(AppVersionService);
  protected readonly offlineQueue = inject(OfflineQueueService);
  readonly clockState = inject(ClockState);

  readonly selectedEmployee = signal<Employee | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  /** True while the backend cannot be reached; drives the offline banner. */
  readonly isOffline = signal(false);

  /**
   * Stamps the server REFUSED during replay (wrong PIN, unknown employee, ...).
   * They are gone from the queue: the time is missing until somebody repairs it
   * in Kimai, so the kiosk has to say so instead of losing it silently
   * (issue #34).
   */
  readonly rejectedStamps = this.offlineQueue.rejected;

  /** Neuester abgelehnter Stempel - das ist der, den der Hinweis zeigt. */
  readonly latestRejectedStamp = computed(() => this.rejectedStamps().at(-1) ?? null);

  /** Stundenübersicht des angemeldeten Mitarbeiters (Heute/Woche/Monat, Netto). */
  readonly hoursOverview = signal<HoursOverview | null>(null);

  private resetTimer: number | null = null;
  private nfcPollTimer: number | null = null;
  /** Interval handle for the local agent scan poll (only while offline). */
  private localNfcTimer: number | null = null;
  private lastNfcEventId: string | null = null;
  private hasInitializedNfcPolling = false;
  /**
   * True while a connectivity loss (failed NFC poll OR an offline-queued
   * action whose request died) has not yet seen a follow-up successful
   * poll. The FIRST successful poll afterwards triggers exactly ONE
   * immediate queue flush. Tracked separately from isOffline on purpose:
   * the banner clears only after the sync really processed an event, so
   * poll success alone is no longer a state edge - keying the flush on
   * isOffline would re-send every second while the server keeps buffering
   * (API up, Kimai down).
   */
  private pendingRecoveryFlush = false;
  private nfcCardId: string | null = null;
  /** Unsubscribes the offline-queue recovery listener (see constructor). */
  private recoveryUnsubscribe: (() => void) | null = null;
  /**
   * Set when an offline-stamped action deliberately skipped the reset to
   * the idle screen (unlocking again needs a PIN login, which is impossible
   * offline). The reset then runs once connectivity has recovered.
   */
  private pendingResetOnRecovery = false;
  private readonly terminalId = this.readTerminalId();
  /** Auto-Reload: Timer-Handle für den verzögerten Reload bei Server-Update. */
  private versionReloadTimer: number | null = null;
  private versionReloadSub: Subscription | null = null;

  constructor() {
    // Hosts without a terminalId (the /clock default route) have no NFC
    // poll, so they never see the connection recover on their own. Use the
    // offline queue's own recovery signal to release a terminal that was
    // kept unlocked for offline stamping and clear the stale banner. The
    // signal fires ONLY when queued events were actually PROCESSED
    // (applied/duplicate/rejected) - a buffered-only flush (API up, Kimai
    // down) emits nothing, so the terminal stays unlocked while a PIN login
    // is still impossible.
    const recoveredSubscription = this.offlineQueue.recovered.subscribe(() => {
      this.isOffline.set(false);
      // Race guard against an in-flight ONLINE action: while a queue flush
      // runs (up to its chunk deadline), the employee can act again because
      // the API answers live. That action owns the screen and arms its OWN
      // teardown in every outcome (success and 4xx via scheduleReset(); the
      // 5xx branch re-queues and re-arms pendingResetOnRecovery itself). A
      // back() here would tear the session out from under the running
      // request: its late response would paint status/message onto the idle
      // screen, and its 2.2 s reset could wipe the next employee's fresh PIN
      // entry. Dropping the deferred reset is safe - the in-flight action
      // supersedes it.
      if (this.isBusy()) {
        this.pendingResetOnRecovery = false;
        return;
      }
      if (this.pendingResetOnRecovery) {
        this.pendingResetOnRecovery = false;
        this.back();
      }
    });
    this.recoveryUnsubscribe = () => recoveredSubscription.unsubscribe();

    // Auto-Reload bei Server-Update: der Kiosk läuft tagelang. Sobald der
    // Server eine ANDERE Version ausliefert als die geladene App, lädt die
    // Seite nach kurzem Hinweis neu (nur im Idle: kein Mitarbeiter
    // angemeldet, keine laufende Aktion, keine PIN-Eingabe). Dev-Builds
    // ('0.0.0-local') sind ausgenommen — dort ist ein Mismatch der Normalfall.
    this.versionReloadSub = this.appVersion.version$.subscribe(version => {
      this.handleServerVersionChange(version);
    });

    if (!this.terminalId) {
      return;
    }

    this.pollNfcEvents();
    this.nfcPollTimer = window.setInterval(() => this.pollNfcEvents(), 1000);
    // Der Agent publiziert Karten NUR an den LocalScanServer (nicht mehr an
    // die API) - der Local-Poll ist damit die einzige Kartenquelle und läuft
    // IMMER, online wie offline. Der Guard in startLocalNfcPolling
    // verhindert einen Doppelstart.
    this.startLocalNfcPolling();
  }

  /**
   * Reagiert auf Server-Versionswechsel: bei Mismatch (und Idle) Hinweis
   * zeigen und nach kurzer Verzögerung neu laden. Der erneute Idle-Check
   * beim Timer-Fire verhindert, dass eine inzwischen gestartete Aktion
   * unterbrochen wird — der nächste Poll (60 s) versucht es dann erneut.
   */
  private handleServerVersionChange(version: string | null): void {
    if (!this.isReleaseBuild()) {
      return; // Dev-Build ('0.0.0-local'): Mismatch ist der Normalfall, nie reloaden
    }
    if (version === null || version === APP_VERSION) {
      return;
    }
    if (this.selectedEmployee() || this.isBusy() || this.pin().length > 0) {
      return; // nicht in eine laufende Interaktion platzen
    }
    this.message.set('Neue Version verfügbar – Aktualisierung...');
    if (this.versionReloadTimer !== null) {
      window.clearTimeout(this.versionReloadTimer);
    }
    this.versionReloadTimer = window.setTimeout(() => {
      this.versionReloadTimer = null;
      if (this.selectedEmployee() || this.isBusy() || this.pin().length > 0) {
        // Abort: inzwischen ist jemand aktiv geworden — den Hinweis wieder
        // entfernen, sonst bleibt er (ohne weiteren Poll) dauerhaft stehen.
        this.message.set('');
        return;
      }
      this.performReload();
    }, VERSION_RELOAD_DELAY_MS);
  }

  /** True im echten Release-Build; getrennt gehalten, damit Tests den Guard überschreiben können. */
  protected isReleaseBuild(): boolean {
    return APP_VERSION !== DEV_VERSION;
  }

  /** Getrennt gehalten, damit Tests den Reload spyen können. */
  protected performReload(): void {
    window.location.reload();
  }

  pressDigit(digit: string): void {
    if (this.isBusy() || this.pin().length >= PIN_LENGTH) {
      return;
    }

    const nextPin = `${this.pin()}${digit}`;
    this.pin.set(nextPin);

    if (nextPin.length === PIN_LENGTH) {
      this.confirmPin();
    }
  }

  clearPin(): void {
    this.pin.set('');
    this.message.set('');
  }

  private loadHoursOverview(pin: string): void {
    if (!pin) {
      return;
    }
    this.kioskApi.hoursOverview(pin).subscribe({
      next: hours => {
        // Stale Responses verwerfen: nach back()/Identitätswechsel ist pin()
        // leer, nach einem neuen Login unterscheidet der PIN - so können die
        // Stunden des Vorgängers nie unter einem anderen Namen erscheinen.
        if (this.pin() === pin) {
          this.hoursOverview.set(hours);
        }
      },
      // Fehler (offline/4xx): Karte bleibt ausgeblendet bzw. zeigt letzte Werte.
      error: () => undefined,
    });
  }

  confirmPin(): void {
    if (!this.pin() || this.isBusy()) {
      return;
    }

    const pin = this.pin();
    this.isBusy.set(true);
    this.message.set('');
    // Neuer Login: nie kurz die Stunden des Vorgängers stehen lassen
    // (in-flight Responses werden zusätzlich per PIN-Guard verworfen).
    this.hoursOverview.set(null);
    this.kioskApi.pinLogin(pin).subscribe({
      next: session => {
        this.selectedEmployee.set(session.employee);
        this.clockState.setStatus(session.status);
        this.clockState.setEmployeeMode(true);
        this.isUnlocked.set(true);
        this.nfcCardId = null;
        this.message.set('');
        this.isBusy.set(false);
        // PIN und Status für den nächsten Ausfall merken: der Kiosk kann sich
        // dann offline anmelden und den plausiblen Stempel-Button anbieten.
        rememberObservedStatus(session.employee.id, session.status);
        void rememberEmployeePin(pin, session.employee);
        this.loadHoursOverview(pin);
      },
      error: (err) => {
        const status = err?.status ?? 0;
        // Network/server errors mean the PIN could NOT be checked - claiming
        // "PIN nicht gefunden" would be wrong and would lock colleagues out
        // of the terminal for the rest of an outage. Fall back to the locally
        // cached verifier instead of refusing the login outright.
        if (status === 0 || status >= 500) {
          void this.confirmPinOffline(pin);
          return;
        }

        // The server rejected this PIN: any cached verifier for it is stale
        // (PIN rotated) and would keep unlocking the kiosk offline while every
        // queued stamp is rejected during replay.
        void forgetEmployeePin(pin);
        this.message.set('PIN nicht gefunden');
        this.pin.set('');
        this.isUnlocked.set(false);
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
      },
    });
  }

  /**
   * Second half of an offline PIN login: the backend could not check the PIN,
   * so resolve it against the verifier cache built from earlier ONLINE logins.
   * The offline path only UNLOCKS - the PIN itself travels with every queued
   * stamp and is validated server-side during replay.
   */
  private async confirmPinOffline(pin: string): Promise<void> {
    const employee = await resolveEmployeeByPin(pin);
    // Der Mitarbeiter kann während des Hashings abgebrochen haben oder eine
    // andere PIN eingegeben haben - dann gehört ihm das Ergebnis nicht.
    if (this.pin() !== pin) {
      this.isBusy.set(false);
      return;
    }

    if (!employee) {
      this.message.set('Offline - PIN kann derzeit nicht geprueft werden.');
      this.pin.set('');
      this.isUnlocked.set(false);
      this.isBusy.set(false);
      this.audioFeedback.playBeeps(2);
      return;
    }

    this.isOffline.set(true);
    this.applyOfflineIdentity(employee, null, pin);
    this.message.set('Offline - mit gemerkter PIN angemeldet.');
    this.isBusy.set(false);
    this.audioFeedback.playBeeps(1);
  }

  start(): void {
    this.sendClockAction('start');
  }

  stop(): void {
    this.sendClockAction('stop');
  }

  startPause(): void {
    this.sendClockAction('pauseStart');
  }

  endPause(): void {
    this.sendClockAction('pauseEnd');
  }

  back(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
      this.resetTimer = null;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.clockState.setEmployeeMode(this.keepFocusedShellAfterReset());
    this.pin.set('');
    this.nfcCardId = null;
    this.isUnlocked.set(false);
    this.message.set('');
    this.isBusy.set(false);
    this.hoursOverview.set(null);
    this.pendingResetOnRecovery = false;
  }

  /**
   * Marks the refused stamps as dealt with. Whoever repaired the missing time
   * in Kimai presses this - without it the notice would nag forever.
   */
  dismissRejectedStamps(): void {
    this.offlineQueue.acknowledgeRejected();
  }

  /** Action wording for the notice about refused stamps. */
  protected rejectedActionLabel(action: RejectedOfflineStamp['action']): string {
    switch (action) {
      case 'start':
        return 'Einstempeln';
      case 'stop':
        return 'Ausstempeln';
      case 'pauseStart':
        return 'Pausenbeginn';
      case 'pauseEnd':
        return 'Pausenende';
      default:
        return 'Stempel';
    }
  }

  ngOnDestroy(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    if (this.nfcPollTimer) {
      window.clearInterval(this.nfcPollTimer);
    }

    if (this.versionReloadTimer !== null) {
      window.clearTimeout(this.versionReloadTimer);
    }
    this.versionReloadSub?.unsubscribe();

    this.stopLocalNfcPolling();

    this.recoveryUnsubscribe?.();

    this.clockState.setEmployeeMode(false);
  }

  private pollNfcEvents(): void {
    if (this.isBusy() || !this.terminalId) {
      return;
    }

    this.kioskApi.latestNfcEvent(this.terminalId).subscribe({
      next: latest => {
        if (this.pendingRecoveryFlush) {
          // Connection just recovered: flush the offline queue ONCE immediately
          // instead of waiting for the retry timer. The deferred back() is NOT
          // done here - the recovered signal above decides, and it fires only
          // when events were actually PROCESSED. The banner clears on the SAME
          // proof: isOffline goes false only when the sync really processed at
          // least one event; a buffered-only flush (API up, Kimai down) keeps
          // it true because a PIN login is still impossible.
          this.pendingRecoveryFlush = false;
          this.offlineQueue.syncNow().subscribe(results => {
            // Leere Queue: nichts nachzutragen - die API antwortet, also
            // online. Buffered-only (API up, Kimai down) bleibt offline,
            // weil ein PIN-Login weiterhin unmöglich ist. Der Local-Poll
            // läuft IMMER weiter - der Agent publiziert nur noch lokal.
            if (results.length === 0 || results.some(result => result.results?.some(detail => detail.status !== 'buffered'))) {
              this.isOffline.set(false);
            }
          });
        }
        this.handleLatestNfcEvent(latest.event);
      },
      error: () => {
        this.hasInitializedNfcPolling = true;
        // Backend unreachable: keep polling (it will recover automatically).
        this.isOffline.set(true);
        this.pendingRecoveryFlush = true;
        // While the backend is down, the local Pi NFC agent becomes the
        // identification source: a scan now only UNLOCKS an employee, the
        // actual stamping happens via the offline-queued buttons.
        this.startLocalNfcPolling();
      },
    });
  }

  private startLocalNfcPolling(): void {
    if (!this.terminalId || this.localNfcTimer !== null) {
      return;
    }

    this.pollLocalScan();
    this.localNfcTimer = window.setInterval(() => this.pollLocalScan(), 1000);
  }

  private stopLocalNfcPolling(): void {
    if (this.localNfcTimer !== null) {
      window.clearInterval(this.localNfcTimer);
      this.localNfcTimer = null;
    }
  }

  private pollLocalScan(): void {
    // Deliberately runs even while isBusy(): kioskApi.clock has no request
    // timeout, so a hung stamp request would keep isBusy true indefinitely
    // and every tap would fall into the agent's fallback (reader blocked
    // for the whole selection timeout, phantom toggle with mode=toggle).
    // Consuming scans is always safe - it only acks, never stamps.
    // Also consume scans while UNLOCKED (offline the kiosk stays unlocked):
    // otherwise every tap blocks the agent's reader loop for the whole
    // selection timeout and - with fallback_mode=toggle - fires a phantom
    // toggle from a possibly stale status cache. We only ack here; no
    // employee switch while an action is in flight.
    if (this.isUnlocked()) {
      this.localNfcScan.poll().subscribe(scan => {
        if (!scan) {
          return;
        }

        this.localNfcScan.ack().subscribe();
        this.message.set(
          'Karte erkannt - bitte zuerst abmelden oder Aktion waehlen.',
        );
      });
      return;
    }

    this.localNfcScan.poll().subscribe(scan => {
      if (!scan) {
        return;
      }

      this.handleLocalScan(scan.cardId);
    });
  }

  /**
   * Resolves a locally scanned card against the cached card -> employee
   * catalog (built from earlier ONLINE NFC events) and unlocks the matched
   * employee without stamping anything.
   */
  private handleLocalScan(cardId: string): void {
    // A card id that normalizes to nothing (only non-hex characters) can
    // never match a cache key - treat it as unknown instead of falling back
    // to the unnormalized raw value (which would bypass the hex/uppercase
    // convention shared with the admin page and the cached keys).
    const normalized = normalizeCardId(cardId);
    const employee = resolveEmployeeByCard(cardId);
    // Consume the scan in every case so the agent does not re-report it.
    this.localNfcScan.ack().subscribe();

    if (employee) {
      this.applyOfflineIdentity(employee, normalized ?? cardId, null);
      this.message.set(`${employee.displayName} - bitte Aktion waehlen.`);
      this.audioFeedback.playBeeps(1);
      // Seit der Local-Poll IMMER läuft, trifft der Cache-Pfad auch online
      // zu - dort ist die API erreichbar, also den frischen Status still
      // nachladen (der Cache kennt nur den letzten Stand). Fehler (429, Netz)
      // ignorieren: der Employee ist bereits freigeschaltet, der Status kommt
      // mit der ersten Aktion.
      if (!this.isOffline()) {
        this.kioskApi.identify(normalized ?? cardId, this.terminalId ?? 'default').subscribe({
          next: event => {
            if (event.success && event.status) {
              this.applyObservedStatus(event.employee?.id ?? employee.id, event.status);
            }
          },
          error: () => {},
        });
      }
      return;
    }

    // Not in the local cache: resolve ONLINE via the server (no stamping).
    // The result is cached so later scans work offline too. Unknown cards
    // and network failures share the same UX as the cache miss.
    // While offline the identify call can only fail (or hang until its
    // timeout) - skip it and answer immediately instead.
    if (this.isOffline()) {
      this.message.set('Unbekannte Karte');
      this.audioFeedback.playBeeps(2);
      return;
    }

    const identifyCardId = normalized ?? cardId;
    this.kioskApi.identify(identifyCardId, this.terminalId ?? 'default').subscribe({
      next: event => {
        if (event.success && event.employee) {
          rememberEmployeeCard(event.cardId ?? identifyCardId, event.employee);
          this.applyOfflineIdentity(event.employee, event.cardId ?? identifyCardId, null, event.status);
          this.message.set(`${event.employee.displayName} - bitte Aktion waehlen.`);
          this.audioFeedback.playBeeps(1);
        } else {
          this.message.set(event.message || 'Unbekannte Karte');
          this.audioFeedback.playBeeps(2);
        }
      },
      error: () => {
        this.message.set('Unbekannte Karte');
        this.audioFeedback.playBeeps(2);
      },
    });
  }

  /**
   * Unlocks the kiosk for an employee identified WITHOUT the backend (local
   * agent scan or cached PIN).
   *
   * `status` is the server's answer when this runs ONLINE; without it the
   * kiosk falls back to the last status it ever saw for this employee, marked
   * as an offline estimate. Knowing nothing at all is a valid outcome (the UI
   * then offers BOTH directions) - never fake "Nicht eingestempelt".
   */
  private applyOfflineIdentity(
    employee: Employee,
    cardId: string | null,
    pin: string | null,
    status?: ClockStatus | null,
  ): void {
    this.selectedEmployee.set(employee);
    this.clockState.setEmployeeMode(true);
    if (status) {
      this.applyObservedStatus(employee.id, status);
    } else {
      // The remembered status is shown while the server's own answer is still
      // on its way - including the case where a hung connection never answers
      // it. The label ("zuletzt gesehen …" / "offline vorgemerkt") keeps the
      // origin visible, so nobody mistakes it for a confirmed booking.
      const cached = lastKnownStatus(employee.id);
      if (cached) {
        this.clockState.setStatus(toOfflineStatus(cached));
      } else {
        // Unknown status: drop the previous employee's state instead of
        // showing a foreign (or invented) one.
        this.clockState.clear();
      }
    }

    this.isUnlocked.set(true);
    // Card sessions stay pin-less (the replay resolves them via the card),
    // a cached-PIN session keeps its PIN so the queued event can be
    // re-validated server-side.
    this.pin.set(pin ?? '');
    this.nfcCardId = cardId;
    // Card login is also an identity switch: never keep the hours of a
    // previous employee (privacy) - and without a pin no reload happens.
    this.hoursOverview.set(null);
  }

  /** Applies a status the SERVER reported and remembers it for the next outage. */
  private applyObservedStatus(employeeId: string, status: ClockStatus): void {
    this.clockState.setStatus(status);
    rememberObservedStatus(employeeId, status);
  }

  private handleLatestNfcEvent(event: NfcClockEvent | null): void {
    if (!this.hasInitializedNfcPolling) {
      this.lastNfcEventId = event?.eventId ?? null;
      this.hasInitializedNfcPolling = true;
      return;
    }

    if (!event || event.eventId === this.lastNfcEventId) {
      return;
    }

    this.lastNfcEventId = event.eventId;
    if (event.success && event.employee && event.status) {
      // Remember the card -> employee pair so a later OFFLINE scan of the
      // same card can still be identified (the backend does that mapping
      // online, but is unreachable then).
      rememberEmployeeCard(event.cardId, event.employee);
      this.selectedEmployee.set(event.employee);
      this.applyObservedStatus(event.employee.id, event.status);
      this.clockState.setEmployeeMode(true);
      this.isUnlocked.set(true);
      this.pin.set('');
      // Identity switch: the previous employee's hours must never stay on
      // screen - the new identity has no pin, so no reload can happen.
      this.hoursOverview.set(null);
      this.nfcCardId = event.cardId;
      this.message.set(event.message);
      this.audioFeedback.playBeeps(1);
      return;
    }

    this.selectedEmployee.set(null);
    this.clockState.clear();
    this.clockState.setEmployeeMode(this.keepFocusedShellAfterReset());
    this.isUnlocked.set(false);
    this.pin.set('');
    // Identity switch: drop any hours of the previous employee (privacy).
    this.hoursOverview.set(null);
    this.nfcCardId = null;
    this.message.set(event.message || 'NFC-Karte nicht erkannt');
    this.audioFeedback.playBeeps(2);
  }

  private sendClockAction(action: 'start' | 'stop' | 'pauseStart' | 'pauseEnd'): void {
    this.isBusy.set(true);
    // Capture the stamp time AND the acting identity SYNCHRONOUSLY at button
    // press: a hanging request (kioskApi.clock has no timeout) reports its
    // failure only seconds to minutes later - and in between a new scan
    // (handleLocalScan), a back() or another unlock may have changed
    // selectedEmployee/pin/nfcCardId. The queued event must describe WHO
    // acted WHEN, so freeze both at press time.
    const performedAt = new Date().toISOString();
    const employeeId = this.selectedEmployee()?.id ?? '';
    // Only for the notice about refused stamps (issue #34): the person has to
    // be named so somebody can repair the missing time in Kimai.
    const employeeName = this.selectedEmployee()?.displayName ?? '';
    const pin = this.pin();
    const nfcCardId = this.nfcCardId;
    this.kioskApi.clock(employeeId, pin, action, nfcCardId).subscribe({
      next: status => {
        this.isOffline.set(false);
        this.clockState.setStatus(status);
        if (employeeId) {
          rememberObservedStatus(employeeId, status);
        }
        this.message.set(status.stateText);
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(1);
        this.loadHoursOverview(pin);
        this.scheduleReset();
      },
      error: (err) => {
        const status = err?.status ?? 0;
        if (status === 0 || status >= 500) {
          // Backend/Kimai unreachable (network error or server failure): queue
          // the action with its real timestamp so it is replayed once
          // connectivity returns. 4xx responses are permanent (wrong PIN,
          // deleted employee, ...) - showing the error is better than queuing
          // an event that can never succeed.
          this.offlineQueue.enqueueKiosk({
            eventId: this.generateEventId(),
            employeeId,
            pin,
            action,
            performedAt,
            // Live-path parity: a session unlocked by NFC touch has NO pin -
            // the replay resolves the employee via the card instead.
            nfcCardId,
            employeeName,
          });
          this.isOffline.set(true);
          // Show where this action leaves the employee instead of keeping the
          // pre-action status on screen: after an offline Einstempeln the
          // kiosk then offers Pause/Ausstempeln instead of another
          // Einstempeln (which would queue a second, redundant start).
          const projected = projectClockStatus(this.clockState.status(), action, performedAt);
          this.clockState.setStatus(withOfflineLabel(projected, 'projected', performedAt));
          if (employeeId) {
            rememberProjectedStatus(employeeId, projected);
          }
          // Let the next successful NFC poll catch the queue up immediately.
          this.pendingRecoveryFlush = true;
          this.message.set(
            'Offline gespeichert - wird automatisch nachgetragen.',
          );
          this.audioFeedback.playBeeps(1);
          // Reset busy state - otherwise the terminal stays locked after the
          // first offline-stamped action (all buttons and the NFC poll check
          // isBusy()).
          this.isBusy.set(false);
          // Stay unlocked instead of resetting to the idle screen: coming
          // back requires a PIN login, and that is impossible while the
          // backend is unreachable - the terminal could then queue exactly
          // ONE stamp per outage. The current employee keeps stamping at
          // THIS terminal (the documented kiosk limitation anyway); once
          // connectivity recovers, the NFC poll runs the deferred back().
          this.pendingResetOnRecovery = true;
          return;
        }

        this.message.set('Kimai konnte nicht speichern');
        this.isBusy.set(false);
        this.audioFeedback.playBeeps(2);
        // Permanent error (wrong PIN, deleted employee, ...): return to the
        // idle screen like every other completed action instead of leaving
        // the message stuck on the terminal.
        this.scheduleReset();
      },
    });
  }

  private generateEventId(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID().replaceAll('-', '');
    }

    return `${Date.now().toString(16)}${Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, '0')}`;
  }

  private scheduleReset(): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    this.resetTimer = window.setTimeout(() => this.back(), 2200);
  }

  private readTerminalId(): string | null {
    const terminalId = this.route.snapshot.queryParamMap.get('terminalId')?.trim();
    return terminalId || null;
  }

  protected keepFocusedShellAfterReset(): boolean {
    return false;
  }
}
