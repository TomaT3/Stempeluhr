# Raspberry Pi NFC-Kiosk

Diese Anleitung beschreibt ein Terminal mit Raspberry Pi 5, 7-Zoll-Touchdisplay,
Chromium im Kiosk-Modus und ACR122U NFC-Reader.

## Empfehlung

- Raspberry Pi OS 64-bit mit Desktop verwenden.
- Die Stempeluhr-Anwendung auf dem NAS betreiben und per URL erreichbar machen.
- Chromium nur als Kiosk-Oberflaeche starten.
- Den ACR122U nicht aus dem Browser ansprechen, sondern ueber den lokalen
  `stempeluhr-nfc-agent`.

Der Agent liest die Karten-UID ueber PC/SC und sendet sie an
`POST /api/nfc/clock` auf der NAS-Stempeluhr. Die Weboberflaeche im
Kiosk-Browser fragt das letzte NFC-Ereignis ueber `GET /api/nfc/events/latest`
ab und zeigt das Ergebnis auf dem Display an. Wichtig: Das passiert nur, wenn
der Browser mit einer `terminalId` gestartet wurde. Normale Clients auf
`/clock` reagieren nicht auf NFC-Ereignisse.

```text
ACR122U -> Pi NFC-Agent -- terminal_id=stempeluhr-pi-01 --> NAS/Stempeluhr API
Chromium Kiosk -- /clock?terminalId=stempeluhr-pi-01 --> NAS/Stempeluhr Weboberflaeche
```

## Warum nicht den PIN auf den Chip schreiben?

PINs gehoeren nicht auf den Chip. Ein einfacher NFC-Tag kann ausgelesen werden;
waere dort der Mitarbeiter-PIN gespeichert, ist der PIN sofort kompromittiert.

Besser:

- Der Chip liefert nur seine UID, zum Beispiel `04AABBCCDD1180`.
- In der Admin-Oberflaeche wird diese Karten-ID einem Mitarbeiter zugeordnet.
- Die API entscheidet serverseitig, welcher Mitarbeiter damit stempeln darf.
- Bei Verlust wird die Karten-ID beim Mitarbeiter entfernt oder ersetzt.

Die UID ist fuer Zutrittskontrolle nicht stark genug, weil einfache Tags
kopierbar sein koennen. Fuer eine Stempeluhr ist das meistens pragmatisch
ausreichend. Wenn Missbrauch ein echtes Risiko ist, spaeter DESFire- oder
andere sichere Karten mit Challenge/Response einplanen.

## Betriebssystem installieren

1. Raspberry Pi Imager installieren:
   <https://www.raspberrypi.com/software/>
2. Raspberry Pi OS 64-bit mit Desktop auswaehlen.
3. In den erweiterten Imager-Optionen direkt setzen:
   - Hostname, zum Beispiel `stempeluhr-pi-01`
   - Benutzer mit starkem Passwort
   - SSH nur aktivieren, wenn Fernwartung gebraucht wird
   - WLAN oder LAN konfigurieren
4. Pi starten, Updates installieren:

```bash
sudo apt update
sudo apt full-upgrade -y
sudo reboot
```

## ACR122U testen

Pakete installieren:

```bash
sudo apt install -y pcscd pcsc-tools python3-pyscard
sudo systemctl enable --now pcscd
```

Reader pruefen:

```bash
pcsc_scan
```

Beim Auflegen eines Chips sollte `pcsc_scan` eine Karte erkennen. Abbrechen mit
`Ctrl+C`.

## Stempeluhr API auf dem NAS fuer NFC absichern

In der API-Konfiguration auf dem NAS sollte ein Reader-Token gesetzt werden.
Beispiel fuer Docker auf dem NAS:

```bash
docker run --rm \
  -p 8080:8080 \
  -v stempeluhr-data:/app/data \
  -e Admin__Password='admin-passwort-aendern' \
  -e Stempeluhr__NfcReaderToken='langes-zufaelliges-token' \
  stempeluhr:local
```

Wenn kein `Stempeluhr:NfcReaderToken` gesetzt ist, akzeptiert die API
NFC-Buchungen nur von `localhost`. Fuer ein echtes Terminal ist ein Token
deshalb erforderlich, weil der Pi die NAS-URL aus dem Netzwerk aufruft.

## NFC-Agent installieren

Systembenutzer anlegen:

```bash
sudo useradd --system --home /nonexistent --shell /usr/sbin/nologin stempeluhr
```

Agent-Dateien kopieren:

```bash
sudo mkdir -p /opt/stempeluhr-nfc-agent /etc/stempeluhr-nfc-agent
sudo cp tools/pi-nfc-agent/stempeluhr_nfc_agent.py /opt/stempeluhr-nfc-agent/
sudo cp tools/pi-nfc-agent/config.example.json /etc/stempeluhr-nfc-agent/config.json
sudo cp tools/pi-nfc-agent/stempeluhr-nfc-agent.service /etc/systemd/system/
sudo chown -R root:root /opt/stempeluhr-nfc-agent
sudo chmod 755 /opt/stempeluhr-nfc-agent/stempeluhr_nfc_agent.py
sudo chmod 600 /etc/stempeluhr-nfc-agent/config.json
```

`/etc/stempeluhr-nfc-agent/config.json` bearbeiten:

```json
{
  "api_base_url": "https://stempeluhr.example.local",
  "terminal_id": "stempeluhr-pi-01",
  "action": "toggle",
  "reader_token": "gleiches-token-wie-in-der-api",
  "debounce_seconds": 3,
  "reader_name_contains": "ACR122"
}
```

Agent starten:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now stempeluhr-nfc-agent
sudo journalctl -u stempeluhr-nfc-agent -f
```

## Karten zuordnen

1. In der Stempeluhr die Admin-Seite oeffnen.
2. Im Bereich `NFC-Kartenleser` die Terminal-ID des Pi eintragen, zum Beispiel
   `stempeluhr-pi-01`.
3. Karte am NFC-Reader auflegen.
4. Die Admin-Seite zeigt die letzte `NFC-Karten-ID` an.
5. Beim passenden Mitarbeiter `Letzte NFC-Karte zuweisen` waehlen.
6. Speichern.

Falls keine Karten-ID erscheint, Agent-Log ansehen:

```bash
sudo journalctl -u stempeluhr-nfc-agent -n 50
```

Die Karten-ID darf nur einmal vergeben sein. Die Admin-Seite und API pruefen
Duplikate.

## Chromium als Kiosk starten

Autostart-Datei fuer den Desktop-Benutzer anlegen:

```bash
mkdir -p ~/.config/autostart
nano ~/.config/autostart/stempeluhr-kiosk.desktop
```

Inhalt:

```ini
[Desktop Entry]
Type=Application
Name=Stempeluhr Kiosk
Exec=chromium-browser --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --app=https://stempeluhr.example.local/clock?terminalId=stempeluhr-pi-01
X-GNOME-Autostart-enabled=true
```

Die `terminal_id` im Agenten und die `terminalId` in der Kiosk-URL muessen
identisch sein. Wenn du spaeter mehrere Terminals hast, bekommt jedes Terminal
einen eigenen Wert, zum Beispiel `lager-eingang` oder `buero-1`.

Danach ab- und wieder anmelden oder neu starten.

## Ausbruch aus dem Kiosk erschweren

Das Ziel ist: Mitarbeitende sehen nur die Stempeluhr. Wartung erfolgt per SSH
oder mit Admin-Tastatur/Benutzer.

Empfohlene Massnahmen:

- Eigenen Desktop-Benutzer fuer den Kiosk verwenden, ohne `sudo`.
- Admin-Benutzer getrennt halten und nur diesem `sudo` erlauben.
- Bildschirm automatisch anmelden lassen, aber nur in den Kiosk-Benutzer.
- SSH nur mit Key-Login aktivieren, Passwort-Login deaktivieren.
- NAS-Stempeluhr nur ueber HTTPS oder ein vertrauenswuerdiges internes Netz
  erreichbar machen.
- NFC-Reader-Token setzen, weil der Pi nicht als `localhost` beim NAS ankommt.
- Keine Tastatur dauerhaft am Terminal lassen.
- Chromium mit `--kiosk --app=...` starten.
- Desktop-Panels und Dateimanager nicht in den Autostart legen.
- Pi-Geraet physisch so montieren, dass USB/SD-Karte nicht frei erreichbar sind.
- Admin-Passwort und NFC-Reader-Token lang und eindeutig setzen.

Optional kann man den Kiosk-Benutzer weiter einschraenken:

```bash
sudo passwd -l kiosk
sudo gpasswd -d kiosk sudo
```

Dabei `kiosk` durch den echten Kiosk-Benutzernamen ersetzen. Nicht fuer den
Admin-Benutzer ausfuehren.

## Fehlerdiagnose

Reader wird nicht gefunden:

```bash
systemctl status pcscd
pcsc_scan
```

Agent meldet API nicht erreichbar:

```bash
curl https://stempeluhr.example.local/api/health
```

Token falsch:

```bash
sudo journalctl -u stempeluhr-nfc-agent -n 50
```

Karte wird erkannt, aber nicht gebucht:

- Admin-Seite oeffnen.
- Terminal-ID im Bereich `NFC-Kartenleser` pruefen.
- Karte erneut auflegen.
- Beim Mitarbeiter `Letzte NFC-Karte zuweisen` waehlen und speichern.
- Karte erneut auflegen.
