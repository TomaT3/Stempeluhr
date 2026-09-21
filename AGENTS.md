# Stempeluhr – Agent Instructions

## Skill-Routing

- Angular unter `stempeluhr-client/`: `angular-developer`
- ASP.NET Core unter `Stempeluhr.Api/`: `dotnet-webapi`
- Tests schreiben oder ändern: `code-testing-agent`
- .NET-Tests ausführen oder bewerten: zuerst `run-tests`
- MSBuild-Probleme: bei Bedarf `binlog-failure-analysis` oder
  `resolve-project-references`

Die API-Tests verwenden xUnit (`[Fact]`, `[Theory]`), nicht MSTest.

## Architektur

- `Stempeluhr.Api`: .NET 10 Minimal API und serverseitiger Kimai-Proxy
- `stempeluhr-client`: Angular-Client für `/clock`, `/terminal` und Admin
- `tools/pi-nfc-agent`: Python-Dienst für ACR122U und lokale NFC-Übergabe
- Ein NFC-Scan identifiziert zunächst den Mitarbeiter. Live gebucht wird erst
  über Clock-/Kiosk-Endpunkte. Offline-Replay ist ein getrennter,
  idempotenter Pfad.
- Secrets bleiben serverseitig in `Stempeluhr.Api/data/settings.json`.

## Build und Test

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

dotnet build Stempeluhr.Api/Stempeluhr.Api.csproj -v q
dotnet test Stempeluhr.Api.Tests/Stempeluhr.Api.Tests.csproj \
  -v minimal --logger "console;verbosity=normal"

cd stempeluhr-client
npm ci
npx ng build --configuration production
npx ng test --watch=false

cd ..
python3 tools/pi-nfc-agent/test_offline_queue.py
bash tools/testenv/run_e2e_test.sh
```

`Stempeluhr.slnx` enthält das Testprojekt nicht; Tests daher über das
Test-`csproj` starten. Befehle nicht durch Pipes führen, die den Exit-Code des
eigentlichen Builds oder Tests verdecken.

## Änderungen und PRs

- Nie direkt auf `main` arbeiten: Feature-/Fix-Branch und Pull Request nutzen.
- Nicht force-pushen. Bestehende Änderungen nicht ungefragt zurücksetzen.
- Abschließende Issues im PR-Text mit `Closes #<issue>` verknüpfen.
- Issue- und PR-Beschreibungen vor der Umsetzung gegen den aktuellen Code
  prüfen.
- Nach einer Merge-Freigabe Merge, Branchlöschung, Release und Image-Status
  verifizieren. Der Kundendeploy bleibt außerhalb dieses Ablaufs.

## Wiederkehrende Invarianten

- Offline-Events geordnet und idempotent verarbeiten.
- Neue `RuntimeSettings`-Felder vollständig durch DTO, Mapping und Admin-Save
  schleusen; Secrets nie zurückgeben.
- Verspätete Status- oder Stundenantworten nach Identitätswechsel verwerfen.
- Queue-Hinweise nicht allein an `isOffline` koppeln.
- Kiosk-Änderungen bei 800×480 auf Scrollen, Fokus und Überdeckung prüfen.
