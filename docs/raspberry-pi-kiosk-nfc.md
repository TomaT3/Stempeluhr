# Raspberry Pi 5 als NFC-Terminal (Bookworm)

Ein Terminal besteht aus Chromium im Kioskmodus (`/terminal?terminalId=<id>`)
und dem NFC-Agenten für den ACR122U. Den Agenten installiert und aktualisiert
der Server selbst: `https://<host>/pi/install.sh` richtet ihn ein, danach hält
ein systemd-Timer ihn auf der Version des Servers.

Im Folgenden steht `<host>` für die externe HTTPS-Adresse der Stempeluhr, z. B.
`stempeluhr.example.com`, und `<id>` für die Terminal-Kennung, z. B.
`stempeluhr-pi-02`.

## 1. Raspberry Pi OS installieren

Im Raspberry Pi Imager:

- Raspberry Pi OS (64-bit) mit Desktop
- Hostname z. B. `stempeluhr-02`
- Benutzer `stempeluhradmin` (Wartung per SSH)
- SSH aktivieren, WLAN/LAN konfigurieren

Nach dem ersten Start:

```bash
sudo apt update && sudo apt full-upgrade -y
sudo apt install -y chromium
sudo reboot
```

## 2. Kiosk-Benutzer und Autologin

```bash
sudo adduser kiosk
sudo gpasswd -d kiosk sudo || true
sudo raspi-config   # System Options -> Boot / Auto Login -> Desktop Autologin
```

Falls nötig in `/etc/lightdm/lightdm.conf`:

```ini
[Seat:*]
autologin-user=kiosk
autologin-user-timeout=0
```

## 3. Agent und Kiosk einrichten

```bash
curl -fsSL https://<host>/pi/install.sh | sudo bash -s -- \
  --server https://<host> --terminal-id <id> --kiosk-user kiosk
```

Der Installer erledigt folgende Schritte und lässt sich gefahrlos erneut
ausführen:

- `pcscd`, `python3-pyscard` und `curl` installieren
- Service-Benutzer `stempeluhr` samt Polkit-Freigabe für den Kartenleser anlegen
- `/etc/stempeluhr-nfc-agent/config.json` anlegen, falls sie fehlt
- Agent nach `/opt/stempeluhr-nfc-agent/releases/<version>` installieren und
  `stempeluhr-nfc-agent.service` starten
- `stempeluhr-nfc-agent-update.timer` aktivieren (kurz nach dem Boot und alle
  15 Minuten)
- Chromium-Autostart für `kiosk` auf `https://<host>/terminal?terminalId=<id>`
- Chromium-Policy `LocalNetworkAccessAllowedForUrls` für `https://<host>`, damit
  die Kiosk-Seite den Agenten auf `127.0.0.1:8737` ohne Nachfrage erreicht

Ein Pi mit einem früher von Hand kopierten Agenten wird mit demselben Befehl
ohne Parameter umgestellt (Werte kommen aus der vorhandenen `config.json`) oder
zentral mit `tools/deploy/pi-deploy.sh bootstrap`.

## 4. Prüfen

```bash
systemctl status stempeluhr-nfc-agent stempeluhr-nfc-agent-update.timer
curl -s http://127.0.0.1:8737/health        # Agent-Version = Server-Version
journalctl -u stempeluhr-nfc-agent -f       # beim Scan: "published and acked by UI"
journalctl -u stempeluhr-nfc-agent-update   # Update-Verlauf
```

Den Kartenleser allein prüft `pcsc_scan` (Beenden mit `Ctrl+C`). Meldet der
Agent `Access denied`, fehlt die Polkit-Regel oder `pcscd` wurde danach nicht
neu gestartet – den Installer einfach erneut ausführen.

## 5. Automatische OS-Sicherheitsupdates

```bash
sudo apt install -y unattended-upgrades
```

`/etc/apt/apt.conf.d/20auto-upgrades`:

```text
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
```

In `/etc/apt/apt.conf.d/50unattended-upgrades` ergänzen bzw. anpassen:

```text
Unattended-Upgrade::Origins-Pattern {
    "origin=Debian,codename=${distro_codename},label=Debian-Security";
    "origin=Debian,codename=${distro_codename}-security,label=Debian-Security";
};
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "02:00";
Unattended-Upgrade::Remove-Unused-Kernel-Packages "true";
Unattended-Upgrade::Remove-New-Unused-Dependencies "true";
Unattended-Upgrade::Mail "";
```

Trockentest: `sudo unattended-upgrade --dry-run --debug`.

## 6. WLAN-Power-Save deaktivieren

Mit aktivem Power-Save verliert der Pi im WLAN oft nach ein bis zwei Stunden
die Verbindung (`iw dev wlan0 get power_save` zeigt `on`).

`/etc/NetworkManager/conf.d/default-wifi-powersave-on.conf`:

```ini
[connection]
wifi.powersave = 2
```

Danach `sudo systemctl restart NetworkManager`; `iw dev wlan0 get power_save`
muss `off` melden.

## Hinweise

- `terminal_id` in `config.json` und `terminalId` in der Kiosk-URL müssen
  gleich sein.
- `api_base_url` ist nur die Basis-Adresse (`https://<host>`), ohne `/terminal`.
- Die Web-App aktualisiert sich nach einem Server-Update selbst (Versions-Poll).
  Hängt ein Kiosk trotzdem auf einer alten App, hilft
  `tools/deploy/pi-deploy.sh kiosk` (Cache-Reset + Reboot).
- Wartung nur über `stempeluhradmin`; `kiosk` braucht keinen SSH-Zugang.
