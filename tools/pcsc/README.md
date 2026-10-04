# PC/SC-Pakete für Stempeluhr-Terminals

Das Docker-Image liefert **pcsc-lite 2.5.2**, Debian-Paketstand
`2.5.2-1~stempeluhr13.1`, für **Debian / Raspberry Pi OS 13 (Trixie), arm64**.
Der Polkit-Speicherfix ist seit
[Upstream 2.5.0](https://github.com/LudovicRousseau/PCSC/releases/tag/2.5.0)
enthalten. Es gibt keinen eigenen Patch an PC/SC. Polkit, libudev und die
vorhandene Berechtigungsregel für `stempeluhr` bleiben erhalten.

## Quelle und Build

`build-packages.sh` verwendet die Originalquelle und Debian-Paketierung aus
dem [Debian-Archiv](https://deb.debian.org/debian/pool/main/p/pcsc-lite/).
Die SHA-256-Werte sind aus dem signierten `pcsc-lite_2.5.2-1.dsc` übernommen
und werden vor dem Entpacken geprüft.
Bei entfernten Dateien dient Debian Snapshot als Fallback; die identischen
SHA-256-Prüfungen gelten auch dort. Die einzige Anpassung der Paketierung
ist debhelper-Kompatibilität 13 für Trixie sowie der Backport-Changelog.
Es werden `pcscd`, `libpcsclite1` und für bereits installierte
Entwicklungspakete `libpcsclite-dev` gebaut. Der Build läuft in einer
Trixie/arm64-Umgebung, nicht gegen Bibliotheken aus Sid auf dem Pi.

### Einmal bauen, per Digest fixieren

Die Pakete werden nicht bei jedem Release kompiliert. `Dockerfile` und
`build-packages.sh` in diesem Verzeichnis sind die Build-Eingaben;
`inputs-hash.sh` bildet daraus einen Hash. Der Workflow
[`pcsc-packages.yml`](../../.github/workflows/pcsc-packages.yml) läuft nur bei
Änderungen an diesen Eingaben, am Haupt-`Dockerfile`, an
`pcsc_maintenance.py` oder am Pakettest. Auf einem arm64-Runner:

1. Passt der Pin im Haupt-`Dockerfile` zum Hash, testet er die fixierten
   Pakete ohne Neubau.
2. Sonst holt er das Image `ghcr.io/tomat3/stempeluhr/pcsc-packages:inputs-<hash>`,
   falls es schon existiert, oder baut die Pakete neu.
3. Er prüft Installation und Rollback (`tools/testenv/test_pcsc_packages.sh`)
   und veröffentlicht neu gebaute Pakete erst danach, unverändert als Image
   unter diesem Tag. Ein vorhandener Tag wird nie ersetzt.
   Läufe mit demselben Eingabe-Hash werden über alle Branches und Auslöser
   hinweg serialisiert. Nur ein bestätigtes fehlendes Manifest erlaubt einen
   Neubau; Netzwerk-, Registry- und Authentifizierungsfehler brechen den Lauf ab.
4. Solange der Pin nicht passt, schlägt der Lauf fehl und nennt in Fehler und
   Zusammenfassung die neue `FROM`-Zeile für das Haupt-`Dockerfile`.

Nach einer Änderung an den Eingaben ist der Ablauf also: pushen, die
gemeldete `FROM`-Zeile übernehmen, erneut pushen. Bei PRs aus Forks wird
nichts veröffentlicht; dann den Workflow per `workflow_dispatch` auf dem
Branch starten. Der Release-Build kopiert nur das fixierte Image und
braucht keine ARM-Emulation.

Lokal die fixierten Pakete exportieren und prüfen (bei privatem Paket vorher
`docker login ghcr.io`):

```bash
docker build --target pcsc-packages --output type=local,dest=artifacts/pcsc .
docker run --rm --platform linux/arm64 \
  --mount type=bind,source="$PWD",target=/work,readonly \
  --workdir /work debian:trixie-slim \
  bash tools/testenv/test_pcsc_packages.sh /work/artifacts/pcsc
```

Pakete lokal aus den Quellen bauen:
`docker build --platform linux/arm64 --output type=local,dest=artifacts/pcsc tools/pcsc`.
Docker Desktop unterstützt die ARM-Emulation; Linux-Buildrechner brauchen
binfmt/QEMU.

Die Quellversion, das Debian-Basisimage per Digest und `SOURCE_DATE_EPOCH`
sind festgelegt. Die `.buildinfo`
enthält die tatsächlich verwendeten Build-Abhängigkeiten. Änderungen dieser
Abhängigkeiten können andere Binärprüfsummen ergeben; ein identischer
Binärbuild über unterschiedliche APT-Stände ist damit nicht zugesichert.
Durch den Digest-Pin bleiben Pakete und Prüfsummen über Releases gleich,
bis sich die Eingaben ändern.
`manifest.json` enthält die Prüfsummen der Pakete. Im Docker-Image
liegen Pakete, Manifest, Buildinfo und Quellprüfsummen im Agent-Bundle; dessen
SHA-256 authentifiziert der bestehende Updater über die Serververbindung.

## Updates und Wartung

Ein bereits installierter Fixstand **>= 2.5.0** wird nicht durch den Backport
ersetzt. Der Installer installiert zuerst Pakete aus den konfigurierten
APT-Quellen; liefert APT einen geeigneten Stand, ist kein Backport nötig.
Die Migration unterstützt zunächst nur Trixie/arm64. Auf anderen Systemen
lassen Installer und Updater pcscd unverändert; ein manuelles `--apply` meldet
dort einen Fehler vor dem Paketwechsel.
`--skip-apt` überspringt die Migration im Installer.

Es wird weder `apt-mark hold` noch ein APT-Pin gesetzt. Ein offizielles Paket
derselben Version ohne `~stempeluhr13.1` oder einer höheren Version löst den
Backport durch die normale Debian-Versionssortierung ab. Ein offizieller
Fixstand mit niedrigerer Versionsnummer erfordert einen bewusst gestarteten,
geprüften Paketwechsel; die Migration führt keine automatischen Downgrades
aus. Sicherheitsupdates für andere Pakete bleiben unabhängig davon möglich.
Auch der Backport muss gepflegt werden: neuere Upstream-/Debian-Fixes prüfen,
Quellstand und Prüfsummen aktualisieren, Pakete neu bauen und testen.

Der Agent-Updater prüft den installierten Fixstand nach jedem erfolgreichen
Lauf. Fehlt er, startet er die Migration einmal pro mitgelieferter
Paketversion und Migrationsrevision als eigenen systemd-Dienst. Revision 2
ermöglicht einen neuen Versuch nach dem fehlgeschlagenen Wechsel mit 0.19.0,
auch bei unveränderter Paketversion und vorhandenem `auto-attempt.json`.
Weitere Timer-Läufe wiederholen einen gescheiterten Versuch derselben Revision
nicht; manuelles `--apply` bleibt möglich. Eine
ältere Container-/Agent-Version rollt PC/SC-Pakete nicht automatisch zurück. Der getrennte Paket-Rollback ist in der
[Pi-Anleitung](../../docs/raspberry-pi-kiosk-nfc.md#pcsc-paketmigration-bestehender-terminals)
beschrieben.

Die Migration installiert eine dauerhafte udev-Regel ausschließlich für den
ACR122U (`072f:2200`, USB-Gerät): `root:pcscd`, Modus `0660`. Sie lädt die
Regeln neu und aktiviert sie für bereits angeschlossene Leser, bevor der
unprivilegierte Dienst startet. Neu angeschlossene Leser erhalten dieselben
Rechte. Dienst und Socket bleiben während des Paketwechsels zur Laufzeit
maskiert; alte PID-/Socket-Dateien und das Laufzeitverzeichnis werden entfernt
und anhand der installierten tmpfiles-/systemd-Regeln neu angelegt. Beim
Rollback wird die vorherige USB-Regel wiederhergestellt bzw. die neue entfernt.
Die Berechtigung für den Agenten bleibt über Polkit geregelt. Das unveränderte,
per Digest fixierte Paketimage erhält die Korrektur über das Agent-Bundle;
ein Paketneubau oder manueller Eingriff am Pi ist nicht erforderlich.

## Stand der Validierung

Paketbuild, Installierbarkeit, Polkit/libudev, GLib-Fixsymbole, Dienst-/Socket-
Benutzer, udev-Regelsyntax, Laufzeitverzeichnis und
Rückinstallation werden in einem wegwerfbaren Trixie/arm64-Container geprüft.
Das ersetzt weder die Prüfung eines ACR122U am Pi noch einen echten Scan
oder die mindestens 24-stündige Speicherbeobachtung für Issue #86.
