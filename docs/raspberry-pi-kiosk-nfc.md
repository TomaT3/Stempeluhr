# Raspberry Pi 5 NFC-Kiosk (Bookworm)

## Ziel

Diese Anleitung richtet einen Raspberry Pi 5 mit Raspberry Pi OS Bookworm als
NFC-Stempeluhr-Terminal ein.

**Eigenschaften**

- Raspberry Pi OS 64-bit mit Desktop
- Chromium im Kioskmodus
- Dedizierte Terminal-Ansicht unter `/terminal`
- ACR122U NFC-Leser
- Lokaler NFC-Agent
- Automatische Anmeldung
- Eigener Kiosk-Benutzer ohne sudo
- Wartung per SSH ueber Admin-Benutzer

------------------------------------------------------------------------

# 1. Raspberry Pi OS installieren

Im Raspberry Pi Imager:

- Raspberry Pi OS (64-bit) mit Desktop
- Hostname z.B. `stempeluhr-01`
- Benutzer `stempeluhradmin`
- SSH aktivieren
- WLAN/LAN konfigurieren

Nach dem ersten Start:

```bash
sudo apt update
sudo apt full-upgrade -y
sudo reboot
```

------------------------------------------------------------------------

# 2. Chromium installieren

```bash
sudo apt install -y chromium
```

------------------------------------------------------------------------

# 3. NFC-Pakete installieren

```bash
sudo apt install -y pcscd pcsc-tools python3-pyscard
sudo systemctl enable --now pcscd
```

Test:

```bash
pcsc_scan
```

Mit `Ctrl+C` beenden.

------------------------------------------------------------------------

# 4. Service-Benutzer anlegen

```bash
sudo useradd --system \
  --home /nonexistent \
  --shell /usr/sbin/nologin \
  stempeluhr
```

PC/SC-Zugriff erlauben:

```bash
sudo tee /etc/polkit-1/rules.d/50-stempeluhr-pcsc.rules >/dev/null <<'EOF'
polkit.addRule(function(action, subject) {
    if ((action.id == "org.debian.pcsc-lite.access_pcsc" ||
         action.id == "org.debian.pcsc-lite.access_card") &&
        subject.user == "stempeluhr") {
        return polkit.Result.YES;
    }
});
EOF

sudo systemctl restart polkit
sudo systemctl restart pcscd
```

Test:

```bash
sudo -u stempeluhr python3 - <<'PY'
from smartcard.System import readers
print(readers())
PY
```

Dieser Test prueft nur, ob der Service-Benutzer den Reader sehen darf. Danach
eine NFC-Karte auflegen und die UID-Abfrage pruefen:

```bash
sudo -u stempeluhr python3 - <<'PY'
from smartcard.System import readers

reader = readers()[0]
connection = reader.createConnection()
connection.connect()
data, sw1, sw2 = connection.transmit([0xFF, 0xCA, 0x00, 0x00, 0x00])
print(data, sw1, sw2)
PY
```

Erwartet ist `144 0` am Ende (`0x90 0x00`). Wenn stattdessen
`Access denied` erscheint, fehlt in der Polkit-Regel meist
`org.debian.pcsc-lite.access_card` oder `pcscd` wurde nach der Regel-Aenderung
noch nicht neu gestartet.

------------------------------------------------------------------------

# 5. Agent installieren

```bash
sudo mkdir -p /opt/stempeluhr-nfc-agent
sudo mkdir -p /etc/stempeluhr-nfc-agent
```

Dateien kopieren.

Konfiguration:

`/etc/stempeluhr-nfc-agent/config.json`

```json
{
  "api_base_url": "https://stempeluhr.example.local",
  "terminal_id": "stempeluhr-pi-01",
  "reader_token": "change-me",
  "debounce_seconds": 3,
  "reader_name_contains": "ACR122"
}
```

Wichtig: `api_base_url` ist nur die Basis-Adresse der Stempeluhr, ohne
`/terminal` oder `/clock`. Der Agent ruft darunter die API-Endpunkte auf.

Wenn der Server auf dem NAS per Docker Compose z.B. mit `8002:8080`
veroeffentlicht wird, aber per Cloudflare/Cloudflared extern ueber HTTPS
erreichbar ist, verwendet der Agent die externe Adresse:

```json
{
  "api_base_url": "https://stempeluhr.example.local",
  "terminal_id": "stempeluhr-pi-01",
  "reader_token": "change-me-reader-token",
  "debounce_seconds": 3,
  "reader_name_contains": "ACR122"
}
```

Rechte setzen:

```bash
sudo chown -R root:root /opt/stempeluhr-nfc-agent
sudo chmod 755 /opt/stempeluhr-nfc-agent/stempeluhr_nfc_agent.py

sudo chown root:stempeluhr /etc/stempeluhr-nfc-agent/config.json
sudo chmod 640 /etc/stempeluhr-nfc-agent/config.json
```

Service aktivieren:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now stempeluhr-nfc-agent
```

------------------------------------------------------------------------

# 6. Kiosk-Benutzer anlegen

```bash
sudo adduser kiosk
sudo gpasswd -d kiosk sudo || true
```

------------------------------------------------------------------------

# 7. Desktop-Autologin

```bash
sudo raspi-config
```

System Options -> Boot / Auto Login -> Desktop Autologin

Falls noetig:

`/etc/lightdm/lightdm.conf`

```ini
[Seat:*]
autologin-user=kiosk
autologin-user-timeout=0
```

------------------------------------------------------------------------

# 8. Chromium-Autostart

```bash
sudo -u kiosk mkdir -p /home/kiosk/.config/autostart
```

Datei:

`/home/kiosk/.config/autostart/stempeluhr-kiosk.desktop`

```ini
[Desktop Entry]
Type=Application
Name=Stempeluhr Kiosk
Exec=chromium --password-store=basic --no-first-run --no-default-browser-check --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --app=https://stempeluhr.example.local/terminal?terminalId=stempeluhr-pi-01
X-GNOME-Autostart-enabled=true
```

Bei einem NAS-Setup hinter Cloudflare/Cloudflared bleibt auch fuer Chromium die
externe HTTPS-Adresse die richtige URL:

```ini
Exec=chromium --password-store=basic --no-first-run --no-default-browser-check --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --app=https://stempeluhr.example.local/terminal?terminalId=stempeluhr-pi-01
```

```bash
sudo chown kiosk:kiosk /home/kiosk/.config/autostart/stempeluhr-kiosk.desktop
```

------------------------------------------------------------------------

# 9. Test

- Chromium startet automatisch auf der Terminal-Route
- Kein Keyring-Dialog
- NFC-Agent laeuft:

```bash
sudo systemctl status stempeluhr-nfc-agent
```

- Logs:

```bash
sudo journalctl -u stempeluhr-nfc-agent -f
```

------------------------------------------------------------------------

# 10. Automatische OS-Sicherheitsupdates

Da der Pi beim Kunden steht, sollen Sicherheitsupdates des Betriebssystems
automatisch eingespielt werden. Dafuer sorgt das Paket `unattended-upgrades`.

**Paket installieren (falls nicht bereits vorhanden):**

```bash
sudo apt install -y unattended-upgrades
```

**Konfiguration – automatische Updates aktivieren:**

`/etc/apt/apt.conf.d/20auto-upgrades`

```text
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
```

**Konfiguration – welche Updates und Reboot-Verhalten:**

`/etc/apt/apt.conf.d/50unattended-upgrades`

Die Datei ist auf dem Pi bereits vorhanden und umfangreich kommentiert.  
Die wesentlichen Einstellungen findest du dort – oder ergaenze/aktualisiere
folgende Bloecke:

```text
// Nur Sicherheitsupdates:
// Origin- und Label-basierte Muster (anstelle des aelteren Allowed-Origins)
Unattended-Upgrade::Origins-Pattern {
    "origin=Debian,codename=${distro_codename},label=Debian-Security";
    "origin=Debian,codename=${distro_codename}-security,label=Debian-Security";
};

// Automatischer Reboot nach Kernel-Updates (z.B. 02:00-04:00 Uhr)
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "02:00";

// Nicht mehr benoetigte Kernel-Pakete und Abhaengigkeiten entfernen
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Remove-New-Unused-Dependencies "true";

// Keine E-Mail-Benachrichtigung (kein Mailserver auf dem Pi)
Unattended-Upgrade::Mail "";
```

> **Hinweis:** Auf Raspberry Pi OS Bookworm (Debian-basiert) wird das
> `-security`-Repo ueber `origin=Debian,...label=Debian-Security` erkannt.
> Welche Origins auf deinem System verfuegbar sind, zeigt
> `apt-cache policy`. Ein manueller Trockentest (s.u.) bestaetigt, ob die
> gewuenschten Pakete gefunden werden.

**Service-Status pruefen:**

```bash
sudo systemctl status unattended-upgrades
```

**Trockentest – ohne tatsaechliche Installation:**

```bash
sudo unattended-upgrade --dry-run --debug
```

Damit siehst du, welche Pakete beim naechsten Lauf installiert wuerrden.

**Logs einsehen:**

```bash
sudo journalctl -u unattended-upgrades -f
```

Die Erklaerung der Konfigurationsoptionen findest du auch direkt in der
Datei `/etc/apt/apt.conf.d/50unattended-upgrades` – dort ist alles
ausfuehrlich kommentiert.

------------------------------------------------------------------------

# 11. WLAN Power-Save deaktivieren

Der Raspberry Pi verliert bei aktiviertem WLAN Power-Saving haeufig nach
einiger Zeit (ca. 1–2 Stunden) die Verbindung. Der Befehl

```bash
iw dev wlan0 get power_save
```

zeigt den aktuellen Status an. Wenn die Ausgabe `Power save: on` lautet, ist
Power-Saving aktiv und sollte deaktiviert werden.

**Power-Save dauerhaft deaktivieren:**

Eine Datei fuer NetworkManager anlegen:

```bash
sudo nano /etc/NetworkManager/conf.d/default-wifi-powersave-on.conf
```

Mit folgendem Inhalt:

```ini
[connection]
wifi.powersave = 2
```

Die Werte bedeuten:

| Wert | Bedeutung                     |
|------|-------------------------------|
| `1`  | Power-Save aktiv (Standard)   |
| `2`  | Power-Save deaktiviert        |
| `3`  | Power-Save aktiv (aggressiv)  |

Danach NetworkManager neu starten:

```bash
sudo systemctl restart NetworkManager
```

Anschliessend pruefen, ob die Aenderung wirksam ist:

```bash
iw dev wlan0 get power_save
```

Die Ausgabe sollte nun `Power save: off` lauten.

------------------------------------------------------------------------

# Hinweise

- `terminal_id` und `terminalId` muessen identisch sein.
- Die Chromium-URL fuer das Display ist die Terminal-Route:
  `https://stempeluhr.example.local/terminal?terminalId=stempeluhr-pi-01`
- Die Agent-Konfiguration verwendet dagegen nur die Basis-URL:
  `https://stempeluhr.example.local`
- NFC-Reader-Token muss mit der API uebereinstimmen.
- Fuer Wartung ausschliesslich `stempeluhradmin` verwenden.
- `kiosk` sollte sich nie per SSH anmelden muessen.
- Automatische OS-Sicherheitsupdates sind via `unattended-upgrades` aktiviert
  (siehe Kapitel 10). Der Pi aktualisiert sich taeglich nachts zwischen 02:00
  und 04:00 Uhr und startet bei Kernel-Updates automatisch neu.
- Die Web-App der Stempeluhr wird serverseitig (z.B. im Docker-Container)
  aktualisiert. Dafuer ist kein Eingriff auf dem Pi noetig.
