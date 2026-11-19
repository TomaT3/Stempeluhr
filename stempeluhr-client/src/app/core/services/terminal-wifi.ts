import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, timeout } from 'rxjs';

import { LOCAL_NFC_SCAN_PORT } from './local-nfc-scan.service';

/** Ein WLAN aus der Suche des Terminals. `security` leer: offenes WLAN. */
export interface WifiNetwork {
  ssid: string;
  signal: number;
  security: string;
  inUse: boolean;
}

export interface WifiStatus {
  device: string | null;
  /** connected, connecting, disconnected, unavailable, ... */
  state: string;
  ssid: string | null;
  ip: string | null;
  connection: string | null;
  /** Verbunden über das Wartungs-WLAN (Handy-Hotspot). */
  serviceWifi: boolean;
  ethernet: boolean;
}

/** Der laufende bzw. letzte Verbindungsversuch im Agenten. */
export interface WifiJob {
  state: 'idle' | 'running' | 'connected' | 'failed';
  ssid?: string;
  message?: string;
}

/** Suche und Verbinden dauern im Helfer selbst bis zu 40 bzw. 55 s. */
const AGENT_TIMEOUT_MS = 15_000;
const SCAN_TIMEOUT_MS = 45_000;

/**
 * WLAN-Einstellung am Display über den lokalen Agenten. Erst die
 * Wartungs-PIN, dann gelten alle Befehle mit der Sitzung. Funktioniert ohne
 * Server: Der Agent prüft die PIN gegen einen lokal abgelegten Hash.
 */
@Injectable({ providedIn: 'root' })
export class TerminalWifiApi {
  private readonly http = inject(HttpClient);
  private readonly port = inject(LOCAL_NFC_SCAN_PORT);

  unlock(pin: string) {
    return this.post<{ session: string }>('/maintenance/unlock', { pin }).pipe(map(answer => answer.session));
  }

  lock(session: string) {
    return this.post<unknown>('/maintenance/lock', { session });
  }

  status(session: string) {
    return this.post<{ status: WifiStatus; job: WifiJob }>('/wifi/status', { session });
  }

  networks(session: string) {
    return this.post<{ networks: WifiNetwork[] }>('/wifi/networks', { session }, SCAN_TIMEOUT_MS).pipe(
      map(answer => answer.networks),
    );
  }

  connect(session: string, ssid: string, psk: string, hidden = false) {
    return this.post<unknown>('/wifi/connect', { session, ssid, psk, hidden });
  }

  private post<T>(path: string, body: object, timeoutMs = AGENT_TIMEOUT_MS) {
    return this.http.post<T>(`http://127.0.0.1:${this.port}${path}`, body).pipe(timeout(timeoutMs));
  }
}
