# Raspberry Pi 5 als NFC-Terminal (Trixie, 64-bit)

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
- Für die mitgelieferte PC/SC-Paketmigration: Debian / Raspberry Pi OS 13
  (Trixie), arm64. Bookworm-Bestandsterminals können den Agenten weiter
  aktualisieren, erhalten aber keinen ungeprüften Trixie-Paketbackport;
  Installer und Updater lassen pcscd dort unverändert.
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

Pro Pi wird ein eigenes zufälliges Token benötigt. Auf einem vertrauenswürdigen
Rechner erzeugen (Datei nur für den Eigentümer lesbar):

```bash
umask 077
python3 -c 'import secrets; print(secrets.token_urlsafe(32))' > terminal.token
```

Den Inhalt in die bestehende Serverdatei `data/settings.json` eintragen,
alle anderen Einstellungen beibehalten:

```json
"terminalTokens": {
  "stempeluhr-pi-02": "<Inhalt der Datei terminal.token>"
}
```

Die Datei über den vorhandenen sicheren Wartungszugang als
`/root/stempeluhr-terminal.token` auf den Pi übertragen (`chmod 600`).
Die Terminal-ID muss in Server-Konfiguration, Agent und Kiosk-URL übereinstimmen.
Das Token nicht in URLs, Browser oder Shell-Befehlsargumente kopieren.
Anschließend installieren:

```bash
curl -fsSL https://<host>/pi/install.sh | sudo bash -s -- \
  --server https://<host> --terminal-id <id> --kiosk-user kiosk \
  --terminal-token-file /root/stempeluhr-terminal.token
```

Nach der Installation die Übertragungsdateien löschen. Das Token bleibt in
`/etc/stempeluhr-nfc-agent/config.json` (`root:stempeluhr`, `640`). Bestehende
Konfigurationen bleiben erhalten; `--terminal-token-file` ergänzt oder ersetzt
nur das Token. Ohne diese Option funktionieren alte Konfigurationen weiter,
der Nachtrag verwendet bis zum ersten erfolgreichen authentifizierten
Katalogabruf weiter PIN/Karte. Bestehende Queue-Zugangsdaten bleiben bis dahin
erhalten. Nach bestätigter Umstellung entfernt der Browser sie dauerhaft;
Token-Entzug bewirkt dann keinen Rückfall auf PIN-Auth.

**Prüfung:** Kiosk einmal online öffnen. Im Browser-Netzwerkprotokoll muss
`http://127.0.0.1:8737/terminal/catalog` mit 200 antworten. Danach Netzwerk
trennen, einen hier noch nie angemeldeten Mitarbeiter per PIN und Karte
anmelden und einen Stempel vormerken. Browser neu starten und Verbindung
wiederherstellen: Der Stempel muss übertragen werden, die gespeicherte Queue
enthält keine PIN. Ein neu installiertes/gelöschtes Browser-Profil benötigt
zuerst diesen Online-Abruf sowie die App im Service Worker.

**Rotation/Entzug:** Token unter `terminalTokens.<id>` auf dem Server ersetzen
oder den Eintrag entfernen; die API liest dies ohne Neustart. Bei Rotation das
neue Token mit `--terminal-token-file` auf den Pi übernehmen und
`sudo systemctl restart stempeluhr-nfc-agent` ausführen. Ein 401 lässt die Queue
unverändert; sie wird mit gültigem Token später nachgetragen. Bereits vom Server
angenommene Outbox-Ereignisse bleiben gültig. Admin-Speichern erhält die Tokens,
Admin-Antworten enthalten nur Terminal-IDs.

Der Installer erledigt folgende Schritte und lässt sich gefahrlos erneut
ausführen:

- `pcscd`, `python3-pyscard` und `curl` installieren
- Service-Benutzer `stempeluhr` samt Polkit-Freigabe für den Kartenleser anlegen
- `/etc/stempeluhr-nfc-agent/config.json` anlegen, falls sie fehlt
- Agent nach `/opt/stempeluhr-nfc-agent/releases/<version>` installieren und
  `stempeluhr-nfc-agent.service` starten
- `stempeluhr-nfc-agent-update.timer` aktivieren (kurz nach dem Boot und alle
  15 Minuten)
- Chromium-Policy `/etc/chromium/policies/managed/stempeluhr.json`
  (`LocalNetworkAccessAllowedForUrls` und `LoopbackNetworkAllowedForUrls` für
  `https://<host>`): Seit Chromium 142 darf eine öffentliche Seite
  `127.0.0.1` nur nach einem Erlaubnis-Dialog erreichen – im Kiosk würde der
  den Kartenleser stilllegen. Wirkt ab dem nächsten Chromium-Start.
- mit `--kiosk-user`: Chromium-Autostart auf
  `https://<host>/terminal?terminalId=<id>`

Ein Pi, der nach der früheren Anleitung von Hand eingerichtet wurde, wird mit
demselben Befehl ohne Parameter umgestellt (Werte kommen aus der vorhandenen
`config.json`, der vorhandene Autostart bleibt) oder zentral mit
`tools/deploy/pi-deploy.sh bootstrap`. Danach einmal neu starten, damit
Chromium die Policy liest. Kontrolle im Kiosk-Browser: `chrome://policy`.

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

## PC/SC-Paketmigration bestehender Terminals

pcscd 2.3.3 (Trixie) verliert bei jeder Polkit-Berechtigungsprüfung Speicher;
der Upstream-Fix ist ab pcsc-lite 2.5.0 enthalten. Das Agent-Bundle bringt
2.5.2 für Trixie/arm64 mit. Eine bereits geeignete Version >= 2.5.0 wird nicht
ersetzt. Details zu Quelle und Sicherheitsupdates:
[PC/SC-Paketversorgung](../tools/pcsc/README.md).

**Der Paketwechsel läuft automatisch.** Nach jedem erfolgreichen Lauf des
Agent-Updaters (Timer alle 15 Minuten) prüft das installierte Bundle den
PC/SC-Stand. Fehlt der Fix, startet der Updater die Migration als eigenen
systemd-Dienst `stempeluhr-pcsc-migration`; sie beginnt, sobald der Updater
fertig ist. Nach einem Server-Deploy ist ein Bestands-Pi damit spätestens
nach zwei Timer-Läufen migriert. Der Installer führt dieselbe Migration
direkt aus und meldet ihr Ergebnis; `--skip-apt` überspringt sie dort.

Pro mitgelieferter Paketversion und Migrationsrevision gibt es genau einen automatischen Versuch.
Scheitert er, wird er zurückgerollt und nicht wiederholt; der Updater meldet
dann bei jedem Lauf „automatische Migration bereits versucht“ im Journal.
Die korrigierte Revision 2 erlaubt auch nach einem Fehlversuch mit 0.19.0 und
altem Marker genau einen neuen Versuch bei derselben Paketversion. Bereits
geeignete Versionen >= 2.5.0 bleiben unverändert.
Als Versuch zählen ein gestarteter Paketwechsel und eine Ablehnung in der
Vorprüfung (z. B. gehaltene Pakete). Fehler beim Herunterladen der
Originalpakete ändern nichts und werden beim nächsten Updater-Lauf wiederholt.
Nach Klärung der Ursache manuell erneut starten:

```bash
sudo python3 /opt/stempeluhr-nfc-agent/current/pcsc_maintenance.py --check
sudo python3 /opt/stempeluhr-nfc-agent/current/pcsc_maintenance.py --apply
```

Während des Paketwechsels werden Agent und pcscd angehalten bzw. neu
gestartet; NFC-Scans sind dabei typischerweise unter einer Minute nicht
möglich. SSH, Tailscale und Netzwerk werden nicht umkonfiguriert; ein
Pi-Neustart ist nicht erforderlich.
Die Migration richtet USB-Rechte ausschließlich für ACR122U-Leser
(`072f:2200`) ein und aktiviert sie auch für bereits angeschlossene Geräte.
Dienst und Socket werden während des Paketwechsels gegen Neustarts gesperrt;
das Laufzeitverzeichnis wird für den jeweiligen Dienstbenutzer neu angelegt.
Beim Rollback wird die vorherige USB-Regel wiederhergestellt bzw. die neue
entfernt. Fehlerausgaben der Leserprüfung stehen im Aktivierungs-/Rollback-Journal.

Vor dem Paketwechsel werden Prüfsummen, Paketmetadaten, Distribution und
Architektur geprüft. Der Paketmanager simuliert die Installation; weitere
Systempakete dürfen dabei weder geändert noch entfernt werden. Die
Rückinstallation setzt damit genau die PC/SC-Pakete auf ihren vorherigen,
lauffähigen Stand zurück.
Originalpakete müssen aus den vorhandenen APT-Quellen herunterladbar sein,
sonst endet die Migration vor jeder Dienstunterbrechung. Falls der lokale
APT-Index veraltet ist, vorher bewusst `sudo apt-get update` ausführen und
erneut prüfen. Gehaltene Pakete und fehlende Unterstützung führen zu einem
verständlichen Fehler. Ein installiertes `libpcsclite-dev` wird zusammen
mit der exakt passenden Bibliothek aktualisiert.

Verändert ein anderer Paketmanager-Lauf die PC/SC-Versionen nach der
Vorprüfung, bricht die Aktivierung vor der Dienstunterbrechung ab. Eine
außerhalb der Migration installierte neuere Version wird auch durch den
Rollback nicht automatisch überschrieben; dann ist manuelle Prüfung nötig.

Originalpakete, PC/SC-Konfigurationsdateien, Agent-Konfiguration und Zustand
liegen geschützt unter `/var/lib/stempeluhr-pcsc-migration/<lauf>/`. Die
Aktivierung läuft als eigener systemd-Dienst weiter, wenn SSH abbricht.
Vorher wird ein Rollback-Timer für 20 Minuten scharf geschaltet; die
Aktivierung darf höchstens 15 Minuten laufen. Schlägt die Prüfung nach der
Aktivierung fehl, startet sofort ein eigener Rückinstallationsdienst ohne
Laufzeitgrenze, damit `dpkg` nie mitten in der Installation abgebrochen wird.
Ein abgebrochener Aktivierungslauf ohne bestätigten Erfolg wird durch den
Timer zurückgerollt; ein unterbrochener `dpkg`-Lauf wird dabei zuerst mit
`dpkg --configure -a` abgeschlossen. Paketwechsel und Rollback warten bis zu
10 Minuten, falls gerade ein anderer Paketmanager-Lauf (z. B.
unattended-upgrades) die dpkg-Sperre hält. Ein Rollback startet den Agenten
auch dann wieder, wenn er scheitert. Fehler beim Rollback müssen anhand des
Dienstjournals behoben werden.

Aktivierung und Rollback-Timer überstehen keinen Neustart. Unterbricht ein
Neustart (z. B. der nächtliche Reboot von unattended-upgrades) die Migration,
erkennt der erste Updater-Lauf nach dem Boot den unvollständigen Lauf und
rollt ihn zurück (`journalctl -u stempeluhr-pcsc-migration`), auch wenn das
Agent-Update dieses Laufs fehlschlägt. Ein manuelles
`--apply` führt diesen Rollback ebenfalls zuerst aus und migriert danach.

Die Prüfung umfasst Paketversion, PC/SC-Leser unter Benutzer `stempeluhr`,
Dienststatus und bei einem vorher laufenden Agenten dessen `/health`. Ein
frisches Browser-Lebenszeichen wird nur verlangt, wenn die Kiosk-Seite schon
vor dem Paketwechsel Lebenszeichen gesendet hat; bei einer Neuinstallation
läuft der Kiosk noch nicht. Ein echter Karten-Scan muss anschließend vor Ort
geprüft werden.

```bash
dpkg-query -W pcscd libpcsclite1
sudo -u stempeluhr python3 /opt/stempeluhr-nfc-agent/current/probe_reader.py ACR122
systemctl status pcscd stempeluhr-nfc-agent
curl -fsS http://127.0.0.1:8737/diagnostics
sudo journalctl -u stempeluhr-pcsc-migration
# <lauf> aus dem Journal bzw. der Ausgabe von --apply übernehmen:
sudo journalctl -u stempeluhr-pcsc-activate-<lauf>.service
```

Ein bewusst gewünschter späterer Paket-Rollback nach erfolgreicher
Aktivierung verwendet die archivierten Originalpakete. Den Erfolgsmerker
zuerst ausdrücklich entfernen, damit der automatische Rollback nach Erfolg
weiterhin ein No-op bleibt:

```bash
sudo mv /var/lib/stempeluhr-pcsc-migration/<lauf>/result.json   /var/lib/stempeluhr-pcsc-migration/<lauf>/result-before-manual-rollback.json
sudo systemd-run --unit=stempeluhr-pcsc-manual-rollback --collect   /usr/bin/python3 /var/lib/stempeluhr-pcsc-migration/<lauf>/pcsc_maintenance.py   --rollback /var/lib/stempeluhr-pcsc-migration/<lauf>
```

Nach einem manuellen Rollback versucht der Updater keine erneute
automatische Migration derselben Paketversion. Eine Rückinstallation stellt
Originalpakete und gesicherte PC/SC-Konfiguration wieder her und prüft Leser
und Dienste erneut; ein fehlendes Browser-Lebenszeichen lässt sie nicht
scheitern. Ein Downgrade des Stempeluhr-Containers selbst
rollt PC/SC nicht zurück.
