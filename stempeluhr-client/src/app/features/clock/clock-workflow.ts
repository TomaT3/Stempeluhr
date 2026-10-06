import { Directive, OnDestroy, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { Observable, Subscription, finalize, map, timeout } from 'rxjs';

import { APP_VERSION, DEV_VERSION } from '../../core/app-version';
import {
  ClockAction,
  ClockStatus,
  CorrectionAuth,
  Employee,
  EmployeeTask,
  HoursOverview,
  WorkTimeHint,
  WorkTimeHints,
  isOnDefaultTask,
} from '../../core/models/kiosk.models';
import { RejectedOfflineStamp } from '../../core/models/offline.models';
import { AppVersionService } from '../../core/services/app-version.service';
import { AudioFeedback } from '../../core/services/audio-feedback';
import { ClockState, projectClockStatus } from '../../core/services/clock-state';
import { KioskApi, isTransientHttpStatus } from '../../core/services/kiosk-api';
import { LocalNfcScanService } from '../../core/services/local-nfc-scan.service';
import { KioskDiagnostics } from '../../core/services/kiosk-diagnostics';
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
import type { CorrectionStart } from './correction-flow/correction-flow';

/** Wartezeit zwischen Versions-Hinweis und Auto-Reload (Mitarbeiter kann abbrechen). */
const VERSION_RELOAD_DELAY_MS = 3000;
/** Neuer Versuch, wenn der Reload gerade nicht ging (Kiosk belegt, Update noch nicht geladen). */
const VERSION_RETRY_MS = 60_000;

const PIN_LENGTH = 4;
/** Abstand des Erreichbarkeits-Polls auf Hosts ohne Kiosk-Poll (§ /clock). */
const HEALTH_POLL_MS = 15_000;
/**
 * Obergrenze für einen Health-Versuch: ohne Timeout stapeln sich Anfragen,
 * wenn der Server TCP annimmt, aber nie antwortet (Review-Befund W2).
 */
const HEALTH_TIMEOUT_MS = 10_000;
/**
 * Sperre nach dem Öffnen einer Auswahl per Knopf: Die Optionen ersetzen den
 * Knopf in derselben Spalte, ein prellender Doppeltipp träfe sonst eine
 * buchende Option (Issue #59).
 */
const CHOICE_TAP_GUARD_MS = 400;
/** Zurück zum Ruhebildschirm nach einer abgeschlossenen Aktion. */
const RESET_MS = 2200;
/** Dasselbe, wenn die Antwort eine Warnung trägt (z. B. abgelehnter Wechsel, Issue #56). */
const WARNING_RESET_MS = 6000;
/** Läuft ein Abschnitt länger, bietet die Sitzung „Vergessen auszustempeln?“ an. */
const FORGOT_STOP_AFTER_SECONDS = 12 * 3600;

/** A stamp as captured at button press (see sendClockAction). */
interface PendingStamp {
  /**
   * Sent with the live request and reused when the stamp is queued: the
   * server can then tell its own half-done transition apart (issue #67).
   */
  eventId: string;
  action: ClockAction;
  performedAt: string;
  employeeId: string;
  employeeName: string;
  pin: string;
  nfcCardId: string | null;
  /** Task of a 'start' or target of a 'switch' (null = default task); frozen with the identity. */
  task: EmployeeTask | null;
}

@Directive()
export abstract class ClockWorkflow implements OnDestroy {
  private readonly kioskApi = inject(KioskApi);
  private readonly audioFeedback = inject(AudioFeedback);
  private readonly route = inject(ActivatedRoute);
  private readonly localNfcScan = inject(LocalNfcScanService);
  private readonly appVersion = inject(AppVersionService);
  private readonly diagnostics = inject(KioskDiagnostics);
  private stopDiagnostics: (() => void) | null = null;
  /** Nur im Produktions-Build registriert (app.config.ts); in Tests/Dev null. */
  private readonly swUpdate = inject(SwUpdate, { optional: true });
  protected readonly offlineQueue = inject(OfflineQueueService);
  readonly clockState = inject(ClockState);

  readonly selectedEmployee = signal<Employee | null>(null);
  readonly pin = signal('');
  readonly isUnlocked = signal(false);
  readonly isBusy = signal(false);
  readonly message = signal('');

  /** True while the backend cannot be reached; drives the offline banner. */
  readonly isOffline = signal(false);
  private readonly replayStatusPending = signal(false);
  private replayStatusRequest: Subscription | null = null;
  readonly actionBlockReason = computed<'none' | 'request' | 'status' | 'backlog'>(() =>
    this.isBusy() ? 'request' : this.replayStatusPending() ? 'status'
      : !this.isOffline() && this.hasPendingForEmployee(this.selectedEmployee()?.id) ? 'backlog' : 'none');
  readonly actionsBlocked = computed(() => this.actionBlockReason() !== 'none');

  private hasPendingForEmployee(employeeId: string | undefined): boolean {
    return !!employeeId && this.offlineQueue.pendingCount().some(entry =>
      entry.event?.employeeId?.toLowerCase() === employeeId.toLowerCase());
  }

  /** Wartende Stempel - der Offline-Banner zeigt sie als Zähler (Issue #6). */
  readonly pendingStamps = computed(() => this.offlineQueue.pendingCount().length);

  /**
   * Bannerzeile, solange Stempel auf die Übertragung warten. Der Banner hängt
   * bewusst NICHT allein an `isOffline`: nimmt Kimai die Nachträge nicht an
   * (die API puffert sie, die Events bleiben in der Queue), muss der Hinweis
   * samt Zähler stehen bleiben, auch wenn die API selbst antwortet
   * (Review-Befund W1). „Offline" steht nur davor, wenn es auch stimmt.
   */
  readonly waitingNotice = computed(() => {
    const pending = this.pendingStamps();
    if (pending === 0) {
      return null;
    }

    const waiting = pending === 1 ? '1 Stempel wartet' : `${pending} Stempel warten`;
    if (this.offlineQueue.needsPin()) return `${waiting} auf Übertragung – PIN erneut eingeben`;
    return this.isOffline() ? `Offline – ${waiting} auf Übertragung` : `${waiting} auf Übertragung`;
  });

  /**
   * Stamps the server REFUSED during replay (wrong PIN, unknown employee, ...).
   * They are gone from the queue: the time is missing until somebody repairs it
   * in Kimai, so the kiosk has to say so instead of losing it silently
   * (issue #34).
   */
  readonly rejectedStamps = this.offlineQueue.rejected;

  /** Neuester abgelehnter Stempel - das ist der, den der Hinweis zeigt. */
  readonly latestRejectedStamp = computed(() => this.rejectedStamps().at(-1) ?? null);
  readonly rejectedStampIds = computed(() => JSON.stringify(this.rejectedStamps().map(stamp => stamp.eventId)));

  /** Stundenübersicht des angemeldeten Mitarbeiters (Heute/Woche/Monat, Netto). */
  readonly hoursOverview = signal<HoursOverview | null>(null);

  /** Weitere Tätigkeiten des angemeldeten Mitarbeiters (leer = kein Wechselknopf). */
  readonly employeeTasks = computed(() => this.selectedEmployee()?.tasks ?? []);

  /** Anzeigename der Haupttätigkeit (im Admin pflegbar, sonst neutral). */
  readonly defaultTaskLabel = computed(() => this.selectedEmployee()?.defaultTaskLabel?.trim() || 'Standard-Tätigkeit');

  /**
   * Arbeit läuft auf der Haupttätigkeit. false auch bei einer Buchung, die
   * keiner Tätigkeit entspricht - dann ist kein Auswahlknopf gesperrt.
   */
  readonly onDefaultTask = computed(() => isOnDefaultTask(this.clockState.status()));

  /** Tätigkeit, auf der gerade gearbeitet wird (weitere, Haupttätigkeit oder unbekannt). */
  readonly currentTaskLabel = computed(() =>
    this.clockState.status()?.activeTaskLabel || (this.onDefaultTask() ? this.defaultTaskLabel() : 'Arbeit'));

  /**
   * Tätigkeitswechsel-Auswahl offen (nur während der Arbeit). Gehört zur
   * Sitzung: jeder Identitätswechsel, back() und jede abgeschickte Aktion
   * schließt sie.
   */
  readonly taskPickerOpen = signal(false);

  /**
   * „Einstempeln“ bei unbekanntem Status gedrückt. Bewusst getrennt von
   * taskPickerOpen: kommt der Status danach als „working“ an, darf daraus
   * nie die Wechsel-Auswahl werden (ein Tipp buchte sonst einen Wechsel).
   */
  private readonly startChoiceOpen = signal(false);

  /**
   * Einstempeln mit Tätigkeitswahl: Ausgestempelt steht die Auswahl sofort
   * da (ein Tipp bucht), bei unbekanntem Status erst nach „Einstempeln“ -
   * dort muss daneben auch „Ausstempeln“ Platz haben.
   */
  readonly startChoiceVisible = computed(() => {
    if (this.employeeTasks().length === 0 || this.clockState.isWorking() || this.clockState.isPaused()) {
      return false;
    }
    return this.clockState.status() !== null || this.startChoiceOpen();
  });

  /**
   * Korrekturablauf (Epic #94) offen. Gehört zur Sitzung wie die Auswahlen:
   * jeder Identitätswechsel, back() und jede Aktion schließt ihn, und mit
   * der Komponente enden ihre laufenden Anfragen - eine verspätete Antwort
   * erreicht nie einen anderen Mitarbeiter.
   */
  readonly correctionOpen = signal(false);
  /** Anmeldung des Ablaufs, beim Öffnen eingefroren wie bei einem Stempel. */
  readonly correctionAuth = signal<CorrectionAuth | null>(null);
  /** Gesetzt, wenn der Ablauf gleich bei „Ausstempeln nachtragen“ für ein Timesheet startet. */
  readonly correctionStart = signal<CorrectionStart | null>(null);
  /**
   * Korrekturen gibt es nur online (keine Queue), und nur ohne laufende
   * Aktion oder wartende Stempel: sonst fehlten dem Antrag deren Zeiten.
   */
  readonly correctionBlocked = computed(() => this.isOffline() || this.actionsBlocked());
  readonly correctionLabel = computed(() => (this.isOffline() ? 'Korrektur nur online' : 'Korrektur'));
  /** Eingestempelt seit über 12 h: vermutlich das Ausstempeln vergessen. */
  readonly forgotToStop = computed(() => {
    const status = this.clockState.status();
    return this.isUnlocked() && status?.state === 'working' && status.activeTimesheetId !== null
      && this.clockState.elapsed() >= FORGOT_STOP_AFTER_SECONDS;
  });

  /**
   * Arbeitszeit-Hinweise des angemeldeten Mitarbeiters (Epic #109). Geladen
   * nach jeder bestätigten Anmeldung, geleert bei jedem Identitätswechsel;
   * Fehler bleiben still, der Hinweis entfällt dann.
   */
  readonly workTimeHints = signal<WorkTimeHints | null>(null);
  private workTimeHintsRequest: Subscription | null = null;

  /**
   * „Pause vergessen?“: die Arbeit läuft seit über 6 h ohne Pause. Nur für
   * den laufenden Eintrag, nur online und ohne wartende Stempel;
   * „Vergessen auszustempeln?“ geht vor.
   */
  readonly pauseHint = computed<WorkTimeHint | null>(() => {
    const status = this.clockState.status();
    if (!this.isUnlocked() || this.correctionBlocked() || this.forgotToStop()
      || status?.state !== 'working' || status.activeTimesheetId === null) {
      return null;
    }
    return this.workTimeHints()?.hints.find(hint =>
      hint.kind === 'continuous' && hint.end === null && hint.timesheetId === status.activeTimesheetId) ?? null;
  });

  /**
   * Hinweis zur letzten Schicht, solange der Mitarbeiter ausgestempelt ist.
   * Der Server liefert nur Fälle der jüngsten Schicht; treffen dort beide
   * zu, geht die Pause vor: dort kann man direkt etwas tun.
   */
  readonly lastShiftHint = computed<WorkTimeHint | null>(() => {
    if (!this.isUnlocked() || this.correctionBlocked() || this.clockState.status()?.state !== 'clockedOut') {
      return null;
    }
    const finished = (this.workTimeHints()?.hints ?? []).filter(hint => hint.end !== null);
    const latest = (kind: WorkTimeHint['kind']) => finished
      .filter(hint => hint.kind === kind)
      .reduce<WorkTimeHint | null>((last, hint) => (last && last.end! >= hint.end! ? last : hint), null);
    return latest('continuous') ?? latest('shift');
  });

  /** „seit 07:58 ohne Pause“ – Beginn in der Kimai-Zeitzone, wie ihn der Server liefert. */
  readonly pauseHintDetail = computed(() => {
    const hint = this.pauseHint();
    return hint ? `seit ${hint.begin.slice(11, 16)} ohne Pause` : '';
  });

  /** „Letzte Schicht: 7:10 Std. ohne Pause“ bzw. „Letzte Schicht: 10:40 Std.“ */
  readonly lastShiftHintLabel = computed(() => {
    const hint = this.lastShiftHint();
    if (!hint) {
      return '';
    }
    const minutes = Math.floor(hint.workedSeconds / 60);
    const hours = `${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')} Std.`;
    return hint.kind === 'continuous' ? `Letzte Schicht: ${hours} ohne Pause` : `Letzte Schicht: ${hours}`;
  });

  private resetTimer: number | null = null;
  /** Erreichbarkeits-Poll des Kiosks (nur mit terminalId). */
  private connectivityPollTimer: number | null = null;
  /** Interval handle for the local agent scan poll. */
  private localNfcTimer: number | null = null;
  /** Ein Poll läuft noch - bei hängender Verbindung keine Anfragen stapeln. */
  private connectivityPollInFlight = false;
  /**
   * True while a connectivity loss (failed poll OR an offline-queued
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
  /**
   * Stille Nachfrage nach einem Kartenscan aus dem Cache. Ihre Antwort
   * aktualisiert immer den Karten-Cache (er gehört zur Karte, nicht zur
   * Sitzung), die Sitzung aber nur, solange `sessionGeneration` unverändert
   * ist: nach einem Identitätswechsel gehört sie zu einer anderen Sitzung,
   * nach einer Aktion ist ihr Status älter als der der Aktion.
   */
  private identifyRefresh: Subscription | null = null;
  /**
   * Zählt resetSessionChoices hoch - bei jedem Identitätswechsel und jeder
   * Aktion -, dazu jeder Scan einer Karte, die nicht im Cache liegt.
   */
  private sessionGeneration = 0;
  /** Bis wann Tipps nach dem Öffnen einer Auswahl verworfen werden (CHOICE_TAP_GUARD_MS). */
  private choiceTapGuardUntil = 0;
  /** Unsubscribes the offline-queue recovery listener (see constructor). */
  private recoveryUnsubscribe: (() => void) | null = null;
  /**
   * Leichter Erreichbarkeits-Poll für Hosts OHNE terminalId (§ /clock, Issue
   * #6): dort gibt es keinen Kiosk-Poll, der das Offline-Banner pflegt.
   */
  private healthPollTimer: number | null = null;
  /** Laufende Health-Anfrage - wird beim Seitenende abgebrochen (Befund R2). */
  private healthCheck: Subscription | null = null;
  /**
   * Set when an offline-stamped action deliberately skipped the reset to
   * the idle screen (unlocking again needs a PIN login, which is impossible
   * offline). The reset then runs once connectivity has recovered.
   */
  private pendingResetOnRecovery = false;
  private readonly terminalId = this.readTerminalId();
  /** `source` der Korrekturanträge: Terminal-ID oder `clock`. */
  readonly correctionSource = this.terminalId ?? 'clock';
  private catalogTimer: number | null = null;
  private catalogRequest: Subscription | null = null;
  /** Auto-Reload: Timer-Handle für den verzögerten Reload bei Server-Update. */
  private versionReloadTimer: number | null = null;
  private versionRetryTimer: number | null = null;
  private versionReloadSub: Subscription | null = null;
  private swUnrecoverableSub: Subscription | null = null;

  constructor() {
    // Hosts without a terminalId (the /clock default route) have no kiosk
    // poll, so they never see the connection recover on their own. Use the
    // offline queue's own recovery signal to release a terminal that was
    // kept unlocked for offline stamping and clear the stale banner. The
    // signal fires ONLY when queued events were actually PROCESSED
    // (applied/duplicate/rejected) - a buffered-only flush (API up, Kimai
    // down) emits nothing, so the terminal stays unlocked while a PIN login
    // is still impossible.
    const recoveredSubscription = this.offlineQueue.recovered.subscribe(() => {
      const wasOffline = this.isOffline();
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
      } else if ((this.replayStatusPending() || wasOffline)
        && this.selectedEmployee() && !this.hasPendingForEmployee(this.selectedEmployee()?.id)) {
        this.refreshStatusAfterReplay();
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
    // Der Service Worker kann seinen Cache nicht mehr bedienen (z. B. Dateien
    // der laufenden Version serverseitig weg): nur ein Reload hilft.
    if (this.swUpdate?.isEnabled) {
      this.swUnrecoverableSub = this.swUpdate.unrecoverable.subscribe(() => this.performReload());
    }

    if (!this.terminalId) {
      // Ohne Kiosk-Poll (§ /clock) merkt die Seite einen Ausfall nur an einer
      // fehlgeschlagenen Aktion - und nach einem Reload bliebe der wartende
      // Stempel unsichtbar. Deshalb ein leichter Health-Poll (Issue #6): er
      // setzt das Banner schon beim Laden und stoesst beim Zurueckkommen den
      // Nachtrag an.
      this.checkHealth();
      this.healthPollTimer = window.setInterval(() => this.checkHealth(), HEALTH_POLL_MS);
      return;
    }

    const refreshCatalog = () => {
      this.catalogRequest?.unsubscribe();
      this.catalogRequest = this.localNfcScan.refreshCatalog().subscribe(catalog => {
        if (catalog !== null) this.offlineQueue.enableTerminalAuth(this.terminalId!);
      });
    };
    if (this.isReleaseBuild()) {
      this.stopDiagnostics = this.diagnostics.start(() => ({
        screen: this.selectedEmployee() ? 'session' : 'idle', busy: this.isBusy(),
        blocked: this.actionBlockReason(),
        offline: this.isOffline(), pending: this.pendingStamps(), rejected: this.rejectedStamps().length,
      }));
    }
    refreshCatalog();
    this.catalogTimer = window.setInterval(refreshCatalog, 60_000);
    this.pollConnectivity();
    this.connectivityPollTimer = window.setInterval(() => this.pollConnectivity(), 1000);
    // Der Agent publiziert Karten NUR an den LocalScanServer - der Local-Poll
    // ist die einzige Kartenquelle und läuft IMMER, online wie offline. Der
    // Guard in startLocalNfcPolling verhindert einen Doppelstart.
    this.startLocalNfcPolling();
  }

  /**
   * Reagiert auf Server-Versionswechsel: bei Mismatch (und Idle) Hinweis
   * zeigen und nach kurzer Verzögerung neu laden. Klappt das gerade nicht
   * (inzwischen jemand aktiv, Service Worker hat die neue Version noch nicht),
   * folgt ein neuer Versuch nach VERSION_RETRY_MS - version$ meldet dieselbe
   * Version kein zweites Mal.
   */
  private handleServerVersionChange(version: string | null): void {
    if (!this.isReleaseBuild()) {
      return; // Dev-Build ('0.0.0-local'): Mismatch ist der Normalfall, nie reloaden
    }
    if (version === null || version === APP_VERSION) {
      return;
    }
    if (!this.isIdle()) {
      this.scheduleVersionRetry();
      return; // nicht in eine laufende Interaktion platzen
    }
    this.message.set('Neue Version verfügbar – Aktualisierung...');
    if (this.versionReloadTimer !== null) {
      window.clearTimeout(this.versionReloadTimer);
    }
    this.versionReloadTimer = window.setTimeout(() => {
      this.versionReloadTimer = null;
      void this.reloadIntoNewVersion();
    }, VERSION_RELOAD_DELAY_MS);
  }

  /**
   * Lädt die App in der neuen Version. Mit Service Worker reicht ein Reload
   * nicht: er lieferte die alte, gecachte App aus - und der Versions-Poll
   * löste sofort den nächsten Reload aus. Deshalb erst die neue Version
   * holen und aktivieren; gelingt das nicht, bleibt die laufende App stehen.
   */
  private async reloadIntoNewVersion(): Promise<void> {
    if (this.swUpdate?.isEnabled) {
      try {
        await this.swUpdate.checkForUpdate();
      } catch {
        this.retryVersionReloadLater();
        return;
      }
    }

    // Während des Update-Checks kann jemand aktiv geworden sein. Erst danach
    // aktivieren: lazy geladene Teile der alten Version fehlen nach der
    // Aktivierung, die App muss dann sofort neu laden.
    if (!this.isIdle()) {
      this.retryVersionReloadLater();
      return;
    }

    if (this.swUpdate?.isEnabled) {
      let activated = false;
      try {
        activated = await this.swUpdate.activateUpdate();
      } catch {
        activated = false;
      }
      if (!activated) {
        this.retryVersionReloadLater();
        return;
      }
    }

    this.performReload();
  }

  private retryVersionReloadLater(): void {
    // Hinweis entfernen, sonst bliebe er bis zum nächsten Versuch stehen.
    this.message.set('');
    this.scheduleVersionRetry();
  }

  private scheduleVersionRetry(): void {
    if (this.versionRetryTimer !== null) {
      return;
    }
    this.versionRetryTimer = window.setTimeout(() => {
      this.versionRetryTimer = null;
      this.handleServerVersionChange(this.appVersion.serverVersion());
    }, VERSION_RETRY_MS);
  }

  /**
   * Kein Mitarbeiter angemeldet, keine laufende Aktion, keine PIN-Eingabe -
   * und kein Korrekturablauf (er gehört zu einer Sitzung, zählt aber
   * ausdrücklich nicht als Ruhezustand).
   */
  private isIdle(): boolean {
    return !this.selectedEmployee() && !this.isBusy() && this.pin().length === 0 && !this.correctionOpen();
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

  /**
   * Lädt die Arbeitszeit-Hinweise der laufenden Sitzung (PIN oder Karte, wie
   * correctionAuth). Die Antwort gilt nur, solange Sitzung und Mitarbeiter
   * dieselben sind: nach einem Identitätswechsel, back() oder einer Aktion
   * wird sie verworfen.
   */
  private loadWorkTimeHints(): void {
    const employeeId = this.selectedEmployee()?.id;
    const pin = this.pin();
    const nfcCardId = this.nfcCardId;
    if (!employeeId || !this.isUnlocked() || this.isOffline() || (!pin && !nfcCardId)) {
      return;
    }
    const generation = this.sessionGeneration;
    this.workTimeHintsRequest?.unsubscribe();
    this.workTimeHintsRequest = this.kioskApi.workTimeHints({ employeeId, pin, nfcCardId }).subscribe({
      next: hints => {
        if (generation === this.sessionGeneration && this.selectedEmployee()?.id === employeeId) {
          this.workTimeHints.set(hints);
        }
      },
      error: () => undefined,
    });
  }

  private clearWorkTimeHints(): void {
    this.workTimeHintsRequest?.unsubscribe();
    this.workTimeHintsRequest = null;
    this.workTimeHints.set(null);
  }

  confirmPin(): void {
    if (!this.pin() || this.isBusy()) {
      return;
    }

    const pin = this.pin();
    const pendingAtLogin = new Set(this.offlineQueue.pendingCount().map(entry => entry.event?.employeeId));
    this.isBusy.set(true);
    this.message.set('');
    // Neuer Login: nie kurz die Stunden des Vorgängers stehen lassen
    // (in-flight Responses werden zusätzlich per PIN-Guard verworfen).
    this.hoursOverview.set(null);
    this.clearWorkTimeHints();
    this.kioskApi.pinLogin(pin).subscribe({
      next: session => {
        this.selectedEmployee.set(session.employee);
        this.resetSessionChoices();
        this.replayStatusPending.set(pendingAtLogin.has(session.employee.id)
          || this.hasPendingForEmployee(session.employee.id));
        if (this.replayStatusPending()) this.clockState.clear();
        else this.clockState.setStatus(session.status);
        this.clockState.setEmployeeMode(true);
        this.isUnlocked.set(true);
        this.nfcCardId = null;
        this.message.set('');
        this.isBusy.set(false);
        // PIN und Status für den nächsten Ausfall merken: der Kiosk kann sich
        // dann offline anmelden und den plausiblen Stempel-Button anbieten.
        if (!this.replayStatusPending()) rememberObservedStatus(session.employee.id, session.status);
        void rememberEmployeePin(pin, session.employee);
        this.offlineQueue.authorizeEmployee(session.employee.id, pin);
        if (this.replayStatusPending()) {
          this.message.set('Ausstehende Stempel werden nachgetragen.');
          if (!this.hasPendingForEmployee(session.employee.id)) this.refreshStatusAfterReplay();
        } else {
          this.loadHoursOverview(pin);
          this.loadWorkTimeHints();
        }
      },
      error: (err) => {
        const status = err?.status ?? 0;
        // Network/server errors (and a Kimai timeout or rate limit) mean the
        // PIN could NOT be checked - claiming "PIN nicht gefunden" would be
        // wrong and would lock colleagues out of the terminal for the rest of
        // an outage. Fall back to the locally cached verifier instead of
        // refusing the login outright.
        if (isTransientHttpStatus(status)) {
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
   * The offline path only UNLOCKS. Terminal replay uses agent authentication;
   * ordinary browser replay requires the employee PIN in memory.
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

  /** Stempelt ein (null = Haupttätigkeit). */
  start(taskId: string | null = null): void {
    this.sendClockAction('start', taskId);
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

  /** Einstempeln-Knopf bei unbekanntem Status: mit weiteren Tätigkeiten erst die Auswahl zeigen. */
  requestStart(): void {
    if (this.employeeTasks().length === 0) {
      this.start();
      return;
    }
    if (!this.isBusy()) {
      this.startChoiceOpen.set(true);
      this.guardChoiceTaps();
    }
  }

  /**
   * Öffnet den Korrekturablauf in der Sitzung. Mit `start` gleich bei
   * „Ausstempeln nachtragen“ für ein Timesheet („Vergessen auszustempeln?“).
   */
  openCorrection(start: CorrectionStart | null = null): void {
    if (!this.selectedEmployee() || !this.isUnlocked() || this.correctionBlocked()) {
      return;
    }
    // Ein Reset nach einer Aktion (2,2 s) würde den Ablauf unter dem Finger schließen.
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
      this.resetTimer = null;
    }
    this.taskPickerOpen.set(false);
    this.startChoiceOpen.set(false);
    this.correctionAuth.set({
      employeeId: this.selectedEmployee()!.id,
      pin: this.pin(),
      nfcCardId: this.nfcCardId,
    });
    this.correctionStart.set(start);
    this.message.set('');
    this.correctionOpen.set(true);
  }

  /** Zurück in die Sitzung; der Mitarbeiter bleibt angemeldet. */
  closeCorrection(): void {
    this.correctionOpen.set(false);
    this.correctionAuth.set(null);
    this.correctionStart.set(null);
  }

  /** „Fertig“ oder „Zurück“ im Ablauf: zurück in die Sitzung, ohne den Hinweis davor („bitte Ende eintragen“). */
  onCorrectionClosed(): void {
    this.closeCorrection();
    this.message.set('');
    // Ein eben gestellter Antrag unterdrückt den Hinweis: nicht den alten
    // Stand antippen lassen, bis die neue Antwort da ist.
    this.clearWorkTimeHints();
    this.loadWorkTimeHints();
  }

  /** 2 min ohne Tipp im Korrekturablauf: zurück in den Ruhezustand. */
  onCorrectionTimedOut(): void {
    this.back();
  }

  /**
   * „Vergessen auszustempeln?“: stempelt über den normalen Stop-Pfad aus
   * (inklusive Offline-Queue) und öffnet nur nach einem online gelungenen
   * Stop „Ausstempeln nachtragen“ für genau dieses Timesheet.
   */
  stopAndCorrect(): void {
    const timesheetId = this.clockState.status()?.activeTimesheetId ?? null;
    if (timesheetId === null) {
      return;
    }
    this.sendClockAction('stop', null, timesheetId);
  }

  /**
   * „Pause vergessen?“: öffnet „Pause nachtragen“ für den laufenden Eintrag.
   * Gestempelt wird dabei nichts; bis zur Freigabe geht es normal weiter.
   */
  openPauseHint(): void {
    const timesheetId = this.pauseHint()?.timesheetId;
    if (timesheetId != null) {
      this.openCorrection({ kind: 'addPause', timesheetId });
    }
  }

  /** Hinweis zur letzten Schicht: „Pause nachtragen“ für den Eintrag, bei über 10 h die Art-Auswahl. */
  openLastShiftHint(): void {
    const hint = this.lastShiftHint();
    if (!hint) {
      return;
    }
    this.openCorrection(hint.kind === 'continuous' && hint.timesheetId !== null
      ? { kind: 'addPause', timesheetId: hint.timesheetId }
      : null);
  }

  /** Zurück von der Einstempel-Auswahl zu Ein-/Ausstempeln (unbekannter Status). */
  closeStartChoice(): void {
    this.startChoiceOpen.set(false);
  }

  openTaskPicker(): void {
    if (this.isBusy() || this.employeeTasks().length === 0 || !this.clockState.isWorking()) {
      return;
    }
    this.taskPickerOpen.set(true);
    this.guardChoiceTaps();
  }

  private guardChoiceTaps(): void {
    this.choiceTapGuardUntil = Date.now() + CHOICE_TAP_GUARD_MS;
  }

  closeTaskPicker(): void {
    this.taskPickerOpen.set(false);
  }

  /** Wechselt ohne Ausstempeln auf eine andere Tätigkeit (null = Standard). */
  switchTask(taskId: string | null): void {
    this.sendClockAction('switch', taskId);
  }

  back(): void {
    this.replayStatusPending.set(false);
    this.replayStatusRequest?.unsubscribe();
    this.replayStatusRequest = null;
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
    this.clearWorkTimeHints();
    this.resetSessionChoices();
    this.pendingResetOnRecovery = false;
  }

  /**
   * Auswahlen gehören zur Sitzung bzw. zum Stand vor einer Aktion. Eine noch
   * laufende stille Nachfrage darf danach nur noch den Karten-Cache
   * aktualisieren (identifyRefresh).
   */
  private resetSessionChoices(): void {
    this.replayStatusRequest?.unsubscribe();
    this.replayStatusRequest = null;
    this.taskPickerOpen.set(false);
    this.startChoiceOpen.set(false);
    this.closeCorrection();
    // Die Auswahl direkt nach einem Login bucht ohne Verzögerung.
    this.choiceTapGuardUntil = 0;
    this.sessionGeneration++;
  }

  /**
   * Marks the refused stamps as dealt with. Whoever repaired the missing time
   * in Kimai presses this - without it the notice would nag forever.
   */
  dismissRejectedStamps(event: Event): void {
    const button = event.currentTarget as HTMLButtonElement;
    const ids = JSON.parse(button.dataset['rejectedIds'] ?? '[]') as string[];
    this.offlineQueue.acknowledgeRejected(ids);
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
      case 'switch':
        return 'Tätigkeitswechsel';
      default:
        return 'Stempel';
    }
  }

  /**
   * Fragt nur die Erreichbarkeit ab (/api/health) und pflegt daraus das
   * Offline-Banner samt Wartezähler (Issue #6, nur auf Hosts ohne
   * terminalId - der Kiosk hat dafür seinen eigenen Poll). Beim Zurückkommen
   * wird der wartende Nachtrag sofort angestoßen.
   */
  private checkHealth(): void {
    // Der Timeout (10 s) ist kuerzer als der Takt (15 s): mehr als eine
    // gleichzeitige Anfrage kann dadurch nicht entstehen, ein eigener
    // In-Flight-Guard waere toter Code (Review-Befund Runde 2).
    this.healthCheck = this.kioskApi
      .health()
      .pipe(timeout(HEALTH_TIMEOUT_MS))
      .subscribe({
        next: () => {
          const wasOffline = this.isOffline();
          this.isOffline.set(false);
          if (wasOffline) {
            this.offlineQueue.syncNow().subscribe();
          }
        },
        // Auch ein Timeout heißt: der Server ist gerade nicht handlungsfähig.
        // Der Banner samt Wartezähler bleibt dann stehen.
        error: () => this.isOffline.set(true),
      });
  }

  ngOnDestroy(): void {
    this.stopDiagnostics?.();
    this.replayStatusRequest?.unsubscribe();
    if (this.catalogTimer !== null) window.clearInterval(this.catalogTimer);
    this.catalogRequest?.unsubscribe();
    if (this.healthPollTimer !== null) {
      window.clearInterval(this.healthPollTimer);
    }
    this.healthCheck?.unsubscribe();
    this.identifyRefresh?.unsubscribe();
    this.workTimeHintsRequest?.unsubscribe();
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    if (this.connectivityPollTimer) {
      window.clearInterval(this.connectivityPollTimer);
    }

    if (this.versionReloadTimer !== null) {
      window.clearTimeout(this.versionReloadTimer);
    }
    if (this.versionRetryTimer !== null) {
      window.clearTimeout(this.versionRetryTimer);
    }
    this.versionReloadSub?.unsubscribe();
    this.swUnrecoverableSub?.unsubscribe();

    this.stopLocalNfcPolling();

    this.recoveryUnsubscribe?.();

    this.clockState.setEmployeeMode(false);
  }

  /**
   * Kiosk-Erreichbarkeit (jede Sekunde): pflegt isOffline und stößt nach
   * einem Ausfall den Nachtrag an. Karten kommen ausschließlich über den
   * lokalen Agenten (pollLocalScan).
   */
  private pollConnectivity(): void {
    if (this.isBusy() || !this.terminalId || this.connectivityPollInFlight) {
      return;
    }

    this.connectivityPollInFlight = true;
    this.kioskApi.ping().pipe(
      finalize(() => (this.connectivityPollInFlight = false)),
    ).subscribe({
      next: () => {
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
      },
      error: () => {
        // Backend unreachable: keep polling (it will recover automatically).
        // Card scans keep unlocking from the local cache; stamps are queued.
        this.isOffline.set(true);
        this.pendingRecoveryFlush = true;
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
    // Deliberately runs even while isBusy(): a stamp request can take up to
    // its timeout, and an unacked scan blocks the agent's reader loop for
    // the whole selection timeout. Consuming scans is always safe - it only
    // acks, never stamps. Also consume scans while UNLOCKED (offline the
    // kiosk stays unlocked) for the same reason. We only ack here; no
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
      const sessionCardId = normalized ?? cardId;
      this.applyOfflineIdentity(employee, sessionCardId, null);
      // Wem die Karte gehört, weiß hier nur der Cache. Stempel mit fehlender
      // PIN bekommen die Karten-ID erst, wenn der Server den Mitarbeiter
      // bestätigt hat - eine umgehängte Karte ließe sonst deren Nachtrag
      // scheitern (Issue #75).
      this.resumeCardBacklog(employee, null);
      this.audioFeedback.playBeeps(1);
      // Seit der Local-Poll IMMER läuft, trifft der Cache-Pfad auch online
      // zu - dort ist die API erreichbar, also Status UND Mitarbeiter still
      // nachladen: der Cache kennt nur den letzten Stand, auch der Tätigkeiten
      // (die Einstempel-Auswahl böte sonst gelöschte an). Fehler (429, Netz)
      // ignorieren: der Employee ist bereits freigeschaltet, der Status kommt
      // mit der ersten Aktion. Nur wenn Stempel auf die Karte warten, bleibt
      // die Sitzung ohne Bestätigung gesperrt - dann sagt die Meldung das.
      // Nach einem Identitätswechsel oder einer Aktion aktualisiert die
      // Antwort nur noch den Karten-Cache (identifyRefresh).
      if (!this.isOffline()) {
        const generation = this.sessionGeneration;
        this.identifyRefresh?.unsubscribe();
        this.identifyRefresh = this.kioskApi.identify(sessionCardId, this.terminalId ?? 'default').subscribe({
          next: event => {
            this.identifyRefresh = null;
            if (!event.success) {
              this.reportUnconfirmedCard(employee, generation);
              return;
            }
            const confirmedCardId = event.cardId ?? sessionCardId;
            if (event.employee) {
              rememberEmployeeCard(confirmedCardId, event.employee);
            }
            if (generation !== this.sessionGeneration) {
              return;
            }
            if (event.employee && event.employee.id !== employee.id) {
              // Karte inzwischen umgehängt: ein Identitätswechsel wie jeder
              // andere - nie Name, Status oder Auswahl des alten stehen lassen.
              this.applyOfflineIdentity(event.employee, sessionCardId, null, event.status);
              this.resumeCardBacklog(event.employee, confirmedCardId);
              if (!this.replayStatusPending()) this.loadWorkTimeHints();
              return;
            }
            if (event.employee) {
              this.selectedEmployee.set(event.employee);
              if (this.hasPendingForEmployee(employee.id)) {
                this.offlineQueue.authorizeEmployeeCard(employee.id, confirmedCardId);
              }
            } else {
              this.reportUnconfirmedCard(employee, generation);
            }
            if (event.status && !this.replayStatusPending()) {
              this.applyObservedStatus(employee.id, event.status);
            }
            // Erst die vom Server bestätigte Karte bekommt Hinweise.
            if (event.employee && !this.replayStatusPending()) this.loadWorkTimeHints();
          },
          error: () => {
            this.identifyRefresh = null;
            this.reportUnconfirmedCard(employee, generation);
          },
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

    // Die Antwort kann bis zum Timeout dauern. Ein neuerer Scan, ein
    // PIN-Login oder back() macht sie zur verspäteten: dann aktualisiert sie
    // nur noch den Karten-Cache, meldet aber weder an noch "Unbekannte Karte"
    // (Issue #28).
    const generation = ++this.sessionGeneration;
    const identifyCardId = normalized ?? cardId;
    this.kioskApi.identify(identifyCardId, this.terminalId ?? 'default').subscribe({
      next: event => {
        if (event.success && event.employee) {
          rememberEmployeeCard(event.cardId ?? identifyCardId, event.employee);
        }
        if (generation !== this.sessionGeneration) {
          return;
        }
        if (event.success && event.employee) {
          this.applyOfflineIdentity(event.employee, event.cardId ?? identifyCardId, null, event.status);
          this.resumeCardBacklog(event.employee, event.cardId ?? identifyCardId);
          if (!this.replayStatusPending()) this.loadWorkTimeHints();
          this.audioFeedback.playBeeps(1);
        } else {
          this.message.set(event.message || 'Unbekannte Karte');
          this.audioFeedback.playBeeps(2);
        }
      },
      error: () => {
        if (generation !== this.sessionGeneration) {
          return;
        }
        this.message.set('Unbekannte Karte');
        this.audioFeedback.playBeeps(2);
      },
    });
  }

  /**
   * Der Server hat die Karte aus dem Cache nicht bestätigt (Fehler, 429 oder
   * unbekannt). Warten Stempel des Mitarbeiters auf die Karte, bleibt die
   * Sitzung gesperrt und nichts wird nachgetragen - „werden nachgetragen"
   * wäre dann falsch (Review zu #75). Die Stempel warten weiter auf eine
   * PIN-Anmeldung oder einen neuen Scan.
   */
  private reportUnconfirmedCard(employee: Employee, generation: number): void {
    if (generation !== this.sessionGeneration || this.selectedEmployee()?.id !== employee.id
      || !this.hasPendingForEmployee(employee.id)) {
      return;
    }
    this.message.set('Karte nicht bestätigt – bitte erneut anmelden.');
  }

  /**
   * `cardId` nur, wenn der Server die Karte diesem Mitarbeiter zugeordnet
   * hat; ohne sie läuft nur der übrige Nachtrag an (Issue #75).
   */
  private resumeCardBacklog(employee: Employee, cardId: string | null): void {
    if (this.replayStatusPending()) {
      this.message.set('Ausstehende Stempel werden nachgetragen.');
      if (cardId) {
        this.offlineQueue.authorizeEmployeeCard(employee.id, cardId);
      } else {
        this.offlineQueue.syncNow().subscribe();
      }
    } else {
      this.message.set(`${employee.displayName} - bitte Aktion waehlen.`);
    }
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
    this.resetSessionChoices();
    this.replayStatusPending.set(!this.isOffline() && this.hasPendingForEmployee(employee.id));
    this.clockState.setEmployeeMode(true);
    if (this.replayStatusPending()) {
      this.clockState.clear();
    } else if (status) {
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
    this.clearWorkTimeHints();
  }

  /** Reload only after this employee's backlog has drained; stale login/status
   * responses must never enable a live stamp that can overtake that backlog. */
  private refreshStatusAfterReplay(): void {
    const employeeId = this.selectedEmployee()?.id;
    if (!employeeId || this.hasPendingForEmployee(employeeId)
      || (this.replayStatusRequest && !this.replayStatusRequest.closed)) return;
    this.replayStatusPending.set(true);
    this.identifyRefresh?.unsubscribe();
    const generation = ++this.sessionGeneration;
    const pin = this.pin();
    const request: Observable<{ employee: Employee | null; status: ClockStatus | null }> = pin ? this.kioskApi.pinLogin(pin).pipe(map(session => ({
      employee: session.employee, status: session.status,
    }))) : this.kioskApi.identify(this.nfcCardId!, this.terminalId ?? 'default');
    this.replayStatusRequest = request.subscribe({
      next: result => {
        if (generation !== this.sessionGeneration || this.selectedEmployee()?.id !== employeeId) return;
        if (result.employee?.id !== employeeId || !result.status) {
          this.back();
          return;
        }
        if (this.hasPendingForEmployee(employeeId)) return;
        this.selectedEmployee.set(result.employee);
        this.applyObservedStatus(employeeId, result.status);
        this.replayStatusPending.set(false);
        this.message.set('');
        this.loadHoursOverview(pin);
        this.loadWorkTimeHints();
      },
      error: () => {
        if (generation !== this.sessionGeneration) return;
        // Stay blocked until another sync or a new login obtains fresh status.
        this.message.set('Status nicht erreichbar – bitte erneut anmelden.');
      },
    });
  }

  /** Applies a status the SERVER reported and remembers it for the next outage. */
  private applyObservedStatus(employeeId: string, status: ClockStatus): void {
    this.clockState.setStatus(status);
    rememberObservedStatus(employeeId, status);
    // Der Status ist jetzt bekannt: die Einstempel-Auswahl für den
    // unbekannten Status ist erledigt, und gewechselt wird nur bei Arbeit.
    this.startChoiceOpen.set(false);
    if (status.state !== 'working') {
      this.taskPickerOpen.set(false);
    }
  }

  private sendClockAction(action: ClockAction, taskId: string | null = null, correctTimesheetId: number | null = null): void {
    if (this.actionsBlocked()) return;
    // Zweiter Tipp kurz nach dem Öffnen einer Auswahl: nicht buchen, die
    // Auswahl bleibt offen. Gilt für jede Aktion - kommt währenddessen der
    // Status an, ersetzen die Stempelknöpfe die Auswahl unter dem Finger.
    if (Date.now() < this.choiceTapGuardUntil) {
      return;
    }
    this.isBusy.set(true);
    this.resetSessionChoices();
    // Nach dem Stop „Vergessen auszustempeln?“ gehört das Ergebnis nur der
    // Sitzung, die ihn ausgelöst hat.
    const generation = this.sessionGeneration;
    // Capture the stamp time AND the acting identity SYNCHRONOUSLY at button
    // press: a request reports its failure only after up to its timeout - and
    // in between a new scan (handleLocalScan), a back() or another unlock may
    // have changed selectedEmployee/pin/nfcCardId. The queued event must
    // describe WHO acted WHEN, so freeze both at press time.
    const stamp: PendingStamp = {
      eventId: this.generateEventId(),
      action,
      performedAt: new Date().toISOString(),
      employeeId: this.selectedEmployee()?.id ?? '',
      // Only for the notice about refused stamps (issue #34): the person has
      // to be named so somebody can repair the missing time in Kimai.
      employeeName: this.selectedEmployee()?.displayName ?? '',
      pin: this.pin(),
      nfcCardId: this.nfcCardId,
      task: taskId === null ? null : (this.employeeTasks().find(task => task.id === taskId) ?? { id: taskId, label: 'Tätigkeit' }),
    };

    // Known outage: queue right away instead of letting the employee wait
    // for a request that can only time out. The polls reset isOffline as soon
    // as the backend answers again (and trigger the replay).
    if (this.isOffline()) {
      this.queueOffline(stamp);
      return;
    }

    this.kioskApi.clock(stamp.employeeId, stamp.pin, action, stamp.nfcCardId, stamp.task?.id ?? null, stamp.eventId).subscribe({
      next: status => {
        this.isOffline.set(false);
        this.clockState.setStatus(status);
        if (stamp.employeeId) {
          rememberObservedStatus(stamp.employeeId, status);
        }
        this.message.set(status.warning || status.stateText);
        this.isBusy.set(false);
        // Anders gebucht als gewählt: wie ein Fehler piepen und die Meldung
        // länger stehen lassen, sonst geht sie im Weggehen unter.
        this.audioFeedback.playBeeps(status.warning ? 2 : 1);
        this.loadHoursOverview(stamp.pin);
        if (correctTimesheetId !== null && !status.warning && generation === this.sessionGeneration
          && this.selectedEmployee()?.id === stamp.employeeId) {
          // Online ausgestempelt: gleich das tatsächliche Ende eintragen,
          // statt in den Ruhezustand zu gehen.
          this.openCorrection({ kind: 'setEnd', timesheetId: correctTimesheetId });
          this.message.set('Ausgestempelt – bitte das tatsächliche Ende eintragen.');
          return;
        }
        this.scheduleReset(status.warning ? WARNING_RESET_MS : RESET_MS);
      },
      error: (err) => {
        const status = err?.status ?? 0;
        if (isTransientHttpStatus(status)) {
          // Backend/Kimai unreachable (network error, timeout, server
          // failure, Kimai 408/429). Other 4xx responses are permanent (wrong
          // PIN, deleted employee, ...) - showing the error is better than
          // queuing an event that can never succeed.
          this.queueOffline(stamp);
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

  /**
   * Queues the action with its real timestamp so it is replayed once
   * connectivity returns, and shows where it leaves the employee.
   */
  private queueOffline(stamp: PendingStamp): void {
    this.offlineQueue.enqueueKiosk({
      ...(this.terminalId ? { terminalId: this.terminalId } : {}),
      eventId: stamp.eventId,
      employeeId: stamp.employeeId,
      pin: stamp.pin,
      action: stamp.action,
      performedAt: stamp.performedAt,
      // Live-path parity: a session unlocked by NFC touch has NO pin - the
      // replay resolves the employee via the card instead.
      nfcCardId: stamp.nfcCardId,
      taskId: stamp.task?.id ?? null,
      employeeName: stamp.employeeName,
    });
    this.isOffline.set(true);
    // Offline gibt es keine Hinweise; nach dem Nachtrag lädt
    // refreshStatusAfterReplay bzw. die nächste Anmeldung sie neu.
    this.clearWorkTimeHints();
    // Show where this action leaves the employee instead of keeping the
    // pre-action status on screen: after an offline Einstempeln the kiosk
    // then offers Pause/Ausstempeln instead of another Einstempeln (which
    // would queue a second, redundant start).
    const projected = projectClockStatus(
      this.clockState.status(),
      stamp.action,
      stamp.performedAt,
      stamp.task,
      this.selectedEmployee()?.defaultTaskLabel?.trim() || null,
    );
    this.clockState.setStatus(withOfflineLabel(projected, 'projected', stamp.performedAt));
    if (stamp.employeeId) {
      rememberProjectedStatus(stamp.employeeId, projected);
    }
    // Let the next successful poll catch the queue up immediately.
    this.pendingRecoveryFlush = true;
    this.message.set('Offline gespeichert - wird automatisch nachgetragen.');
    this.audioFeedback.playBeeps(1);
    // Reset busy state - otherwise the terminal stays locked after the first
    // offline-stamped action (all buttons and the connectivity poll check isBusy()).
    this.isBusy.set(false);
    // Stay unlocked instead of resetting to the idle screen: the current
    // employee may want to stamp again (e.g. pause) while the backend is
    // still unreachable. Once connectivity recovers, the recovered signal
    // runs the deferred back().
    this.pendingResetOnRecovery = true;
  }

  private generateEventId(): string {
    if (typeof crypto !== 'undefined' && 'randomUUID' in crypto) {
      return crypto.randomUUID().replaceAll('-', '');
    }

    return `${Date.now().toString(16)}${Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, '0')}`;
  }

  private scheduleReset(delayMs = RESET_MS): void {
    if (this.resetTimer) {
      window.clearTimeout(this.resetTimer);
    }

    this.resetTimer = window.setTimeout(() => this.back(), delayMs);
  }

  private readTerminalId(): string | null {
    const terminalId = this.route.snapshot.queryParamMap.get('terminalId')?.trim();
    return terminalId || null;
  }

  protected keepFocusedShellAfterReset(): boolean {
    return false;
  }
}
