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
    if (action.id == "org.debian.pcsc-lite.access_pcsc" &&
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
  "action": "toggle",
  "reader_token": "change-me",
  "debounce_seconds": 3,
  "reader_name_contains": "ACR122"
}
```

Wichtig: `api_base_url` ist nur die Basis-Adresse der Stempeluhr, ohne
`/terminal` oder `/clock`. Der Agent ruft darunter die API-Endpunkte auf.

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

# Hinweise

- `terminal_id` und `terminalId` muessen identisch sein.
- Die Chromium-URL fuer das Display ist die Terminal-Route:
  `https://stempeluhr.example.local/terminal?terminalId=stempeluhr-pi-01`
- Die Agent-Konfiguration verwendet dagegen nur die Basis-URL:
  `https://stempeluhr.example.local`
- NFC-Reader-Token muss mit der API uebereinstimmen.
- Fuer Wartung ausschliesslich `stempeluhradmin` verwenden.
- `kiosk` sollte sich nie per SSH anmelden muessen.
