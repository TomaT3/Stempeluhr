# PC/SC-Pakete für Stempeluhr-Terminals

Das Docker-Image baut **pcsc-lite 2.5.2**, Debian-Paketstand
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

```bash
docker build -f tools/pcsc/Dockerfile --output type=local,dest=artifacts/pcsc .
docker run --rm --platform linux/arm64 \
  --mount type=bind,source="$PWD",target=/work,readonly \
  --workdir /work debian:trixie-slim \
  bash tools/testenv/test_pcsc_packages.sh /work/artifacts/pcsc
```

Docker Desktop unterstützt die ARM-Emulation; Linux-Buildrechner brauchen
binfmt/QEMU oder einen nativen arm64-Runner. Der Release-Workflow richtet
die Emulation ein. Die CI baut und prüft Pakete auf einem arm64-Runner.

Die Quellversion, das Debian-Basisimage per Digest und `SOURCE_DATE_EPOCH`
sind festgelegt. Die `.buildinfo`
enthält die tatsächlich verwendeten Build-Abhängigkeiten. Änderungen dieser
Abhängigkeiten können andere Binärprüfsummen ergeben; ein identischer
Binärbuild über unterschiedliche APT-Stände ist damit nicht zugesichert.
`manifest.json` enthält die Prüfsummen des jeweiligen Builds. Im Docker-Image
liegen Pakete, Manifest, Buildinfo und Quellprüfsummen im Agent-Bundle; dessen
SHA-256 authentifiziert der bestehende Updater über die Serververbindung.

## Updates und Wartung

Ein bereits installierter Fixstand **>= 2.5.0** wird nicht durch den Backport
ersetzt. Der Installer installiert zuerst Pakete aus den konfigurierten
APT-Quellen; liefert APT einen geeigneten Stand, ist kein Backport nötig.
Die explizite Migration unterstützt zunächst nur Trixie/arm64. Andere
Systeme ohne geeigneten Fixstand melden einen Fehler vor dem Paketwechsel.
`--skip-apt` überspringt auch die Migration.

Es wird weder `apt-mark hold` noch ein APT-Pin gesetzt. Ein offizielles Paket
derselben Version ohne `~stempeluhr13.1` oder einer höheren Version löst den
Backport durch die normale Debian-Versionssortierung ab. Ein offizieller
Fixstand mit niedrigerer Versionsnummer erfordert einen bewusst gestarteten,
geprüften Paketwechsel; die Migration führt keine automatischen Downgrades
aus. Sicherheitsupdates für andere Pakete bleiben unabhängig davon möglich.
Auch der Backport muss gepflegt werden: neuere Upstream-/Debian-Fixes prüfen,
Quellstand und Prüfsummen aktualisieren, Pakete neu bauen und testen.

Der normale Agent-Updater prüft den installierten Fixstand bei jedem Lauf
und meldet einen fehlenden Fix. Er installiert keine Betriebssystempakete.
Eine ältere Container-/Agent-Version rollt PC/SC-Pakete nicht automatisch
zurück. Der getrennte Paket-Rollback ist in der
[Pi-Anleitung](../../docs/raspberry-pi-kiosk-nfc.md#pcsc-paketmigration-bestehender-terminals)
beschrieben.

## Stand der Validierung

Paketbuild, Installierbarkeit, Polkit/libudev, GLib-Fixsymbole und
Rückinstallation werden in einem wegwerfbaren Trixie/arm64-Container geprüft.
Das ersetzt weder die Prüfung eines ACR122U am Pi noch einen echten Scan
oder den mindestens 24-stündigen Speichervergleich für Issue #86.
