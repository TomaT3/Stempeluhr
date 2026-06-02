# Stempeluhr fuer Kimai

Aktuelle Version: `0.1.2`

Touch-freundliche Stempeluhr fuer eine gehostete Kimai-Instanz.

## Aufbau

- `Stempeluhr.Api`: .NET Minimal API als sicherer Kimai-Proxy
- `stempeluhr-client`: Angular-App fuer die Mitarbeiteroberflaeche

Die Kimai-API-Tokens bleiben im Backend. Der Browser bekommt nur Namen, Farben und Statusdaten.

## Kimai konfigurieren

Die Kimai-Adresse, Admin-Tokens, Mitarbeiter-Tokens und Bilder werden nicht in Git gespeichert.
Die App schreibt diese Werte lokal in `Stempeluhr.Api/data/settings.json`; der Ordner `data/` ist ignoriert.

Lokal kannst du `Stempeluhr.Api/appsettings.Development.json` verwenden. Diese Datei ist ebenfalls ignoriert.
Als Vorlage gibt es `Stempeluhr.Api/appsettings.Development.example.json`.

Minimaler lokaler Start:

```json
{
  "Admin": {
    "Password": "admin"
  },
  "Kimai": {
    "BaseUrl": "https://kimai.example.invalid"
  }
}
```

Danach in der App oben `Admin` oeffnen:

- Kimai-URL und Admin-API-Token setzen
- Kimai-Mitarbeiter laden
- pro Mitarbeiter API-Token, PIN, Farbe und optional Bild pflegen
- Standard-Projekt-ID und Standard-Aktivitaet-ID setzen

`Pin` ist optional. Ohne PIN kann ein Mitarbeiter direkt ein- und ausstempeln.

## Starten

Backend:

```powershell
dotnet run --project .\Stempeluhr.Api\Stempeluhr.Api.csproj
```

Frontend:

```powershell
cd .\stempeluhr-client
npm install
npm start
```

Danach ist die App unter `http://localhost:4200` erreichbar. Das Backend laeuft auf `http://localhost:5100`.
Der Angular-Dev-Server leitet `/api` ueber `stempeluhr-client/proxy.conf.json` lokal an das Backend weiter.

## Docker

Image lokal bauen:

```powershell
docker build -t stempeluhr:0.1.2 .
```

Container starten:

```powershell
docker run --rm -p 8080:8080 -v stempeluhr-data:/app/data -e Admin__Password=admin stempeluhr:0.1.2
```

Die App ist dann unter `http://localhost:8080` erreichbar.
Im Container werden Frontend und Backend vom selben .NET-Prozess ausgeliefert. Dadurch funktionieren API-Aufrufe relativ ueber `/api`, auch wenn der Container spaeter ueber Cloudflared unter einer externen Domain erreichbar ist.

## Semantische Versionierung

Versionen folgen SemVer: `MAJOR.MINOR.PATCH`.
Die Versionsdateien werden nicht automatisch durch einen Git-Tag geaendert. Vor einem Release muessen die Dateien angepasst und committed werden; der Tag startet danach den Container-Release.

- `VERSION` enthaelt die aktuelle App-Version.
- `Directory.Build.props` setzt die .NET Assembly-Version.
- `stempeluhr-client/package.json` enthaelt die Angular-Version.

Ein Release wird ueber einen Git-Tag erstellt:

```powershell
git tag v0.1.2
git push origin v0.1.2
```

Der Workflow `.github/workflows/release-container.yml` baut bei Tags wie `v0.1.2` oder bei einem veroeffentlichten GitHub Release ein Docker-Image und pusht es nach GitHub Container Registry:

```text
ghcr.io/<owner>/<repo>:0.1.2
ghcr.io/<owner>/<repo>:0.1
ghcr.io/<owner>/<repo>:latest
```

## Kimai-Endpunkte

Die App verwendet serverseitig diese Kimai-Endpunkte:

- `GET /api/timesheets/active`
- `POST /api/timesheets`
- `PATCH /api/timesheets/{id}/stop`
