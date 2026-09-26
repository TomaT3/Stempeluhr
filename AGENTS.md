# Stempeluhr – Hinweise für Coding-Agents

Architektur, Abläufe, Build und Tests stehen in der [README](README.md). Hier
nur, was zusätzlich für Änderungen gilt.

## Arbeitsweise

- Nie direkt auf `main`: Feature-/Fix-Branch und Pull Request. Nicht
  force-pushen, bestehende Änderungen nicht ungefragt zurücksetzen.
- Issue- und PR-Beschreibungen vor der Umsetzung gegen den aktuellen Code
  prüfen; abschließende Issues im PR-Text mit `Closes #<issue>` verknüpfen.
- API-Tests sind xUnit (`[Fact]`, `[Theory]`) und laufen über
  `Stempeluhr.Api.Tests/Stempeluhr.Api.Tests.csproj` (nicht über die `.slnx`).
- Build- und Testbefehle nicht durch Pipes führen, die den Exit-Code verdecken.
- Der Kunden-Deploy ist nicht Teil eines PRs.

## Invarianten

- Ein NFC-Scan identifiziert nur; gebucht wird ausschließlich über
  `/api/kiosk/clock` bzw. den Offline-Nachtrag `/api/kiosk/clock/sync`.
- Offline-Events geordnet und idempotent verarbeiten; jede nachgetragene
  Aktion muss gegen den Kimai-Status zum No-op werden können.
- Neue `RuntimeSettings`-Felder vollständig durch DTO, Mapping und Admin-Save
  schleusen; Secrets nie zurückgeben.
- Verspätete Status- oder Stundenantworten nach Identitätswechsel verwerfen.
- Queue-Hinweise nicht allein an `isOffline` koppeln.
- Agent, `update.sh` und Units in `tools/pi-nfc-agent` landen über das
  Docker-Image auf den Pis: abwärtskompatibel zu vorhandenen `config.json`
  bleiben und `tools/testenv/test_pi_update.sh` grün halten.
- Kiosk-Änderungen bei 800×480 auf Scrollen, Fokus und Überdeckung prüfen.
