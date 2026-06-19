# Stempeluhr fuer Kimai

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
- Standard-Projekt-ID, Standard-Aktivitaet-ID und Pause-Aktivitaet-ID setzen

`Pin` ist optional. Ohne PIN kann ein Mitarbeiter direkt ein- und ausstempeln.
Die Pause-Aktivitaet-ID verweist auf eine normale Kimai-Taetigkeit, die als Pause genutzt wird.
Aktive Timesheets mit dieser Taetigkeit werden in der Stempeluhr als `In Pause` angezeigt.

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
Der Angular-Dev-Server leitet `/api` ueber `stempeluhr-client/proxy.conf.json` lokal an das Backend weiter. Das gilt auch fuer direktes `ng serve`, weil der Proxy in `angular.json` eingetragen ist.

## Docker

Image lokal bauen:

```powershell
docker build -t stempeluhr:local .
```

Container starten:

```powershell
docker run --rm -p 8080:8080 -v stempeluhr-data:/app/data -e Admin__Password=admin stempeluhr:local
```

Die App ist dann unter `http://localhost:8080` erreichbar.
Im Container werden Frontend und Backend vom selben .NET-Prozess ausgeliefert. Dadurch funktionieren API-Aufrufe relativ ueber `/api`, auch wenn der Container spaeter ueber Cloudflared unter einer externen Domain erreichbar ist.

## Semantische Versionierung

Versionen folgen SemVer: `MAJOR.MINOR.PATCH`. Die Release-Version ist der Git-Tag, zum Beispiel `v0.1.3`.

Es gibt keine Versionsdatei, die pro Release angepasst werden muss:

- Lokale Builds verwenden `0.0.0-local`.
- Release-Builds bekommen die Version aus dem Git-Tag.
- Die GitHub Action uebergibt die Tag-Version als Docker-Build-Arg an `.NET`.
- `stempeluhr-client/package.json` bleibt bei `0.0.0`, weil die Angular-App nicht als npm-Paket released wird.

Automatisch versionieren:

1. In GitHub `Actions` oeffnen.
2. Workflow `Create version tag` starten.
3. `patch`, `minor` oder `major` waehlen.
4. Der Workflow erzeugt den naechsten Tag, zum Beispiel `v0.1.3`.
5. Der Workflow `Release container` baut und pusht danach automatisch das Docker-Image.

Manuell geht es weiterhin ueber einen Git-Tag:

```powershell
git tag v0.1.3
git push origin v0.1.3
```

Der Workflow `.github/workflows/release-container.yml` baut bei Tags wie `v0.1.3` oder bei einem veroeffentlichten GitHub Release ein Docker-Image und pusht es nach GitHub Container Registry:

```text
ghcr.io/<owner>/<repo>:0.1.3
ghcr.io/<owner>/<repo>:0.1
ghcr.io/<owner>/<repo>:latest
```

## Kimai-Endpunkte

Die App verwendet serverseitig diese Kimai-Endpunkte:

- `GET /api/timesheets/active`
- `POST /api/timesheets`
- `PATCH /api/timesheets/{id}/stop`
