# syntax=docker/dockerfile:1

FROM node:24-alpine AS client-build
ARG VERSION=0.0.0-local
WORKDIR /src/stempeluhr-client
COPY stempeluhr-client/package*.json ./
RUN npm ci
COPY stempeluhr-client/ ./
# Client-Version aus dem Release-Tag in die App injizieren (Version-Badge auf
# Terminal-/Admin-Seite). Default bleibt '0.0.0-local' für lokale Builds.
RUN sed -i "s|export const APP_VERSION = '0.0.0-local'|export const APP_VERSION = '${VERSION}'|" src/app/core/app-version.ts
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api-build
ARG VERSION=0.0.0-local
WORKDIR /src
COPY Directory.Build.props ./
COPY Stempeluhr.Api/Stempeluhr.Api.csproj Stempeluhr.Api/
RUN dotnet restore Stempeluhr.Api/Stempeluhr.Api.csproj
COPY Stempeluhr.Api/ Stempeluhr.Api/
COPY --from=client-build /src/stempeluhr-client/dist/stempeluhr-client/browser Stempeluhr.Api/wwwroot
RUN dotnet publish Stempeluhr.Api/Stempeluhr.Api.csproj \
    -c Release \
    -o /app/publish \
    /p:Version=${VERSION} \
    /p:InformationalVersion=${VERSION} \
    --no-restore

# Agent-Bundle für die Pi-Terminals. Der Server liefert es unter /pi/ aus,
# die Pis holen es sich per update.sh selbst - Agent-Version = Server-Version.
FROM alpine:3.22 AS pi-bundle
ARG VERSION=0.0.0-local
COPY tools/pi-nfc-agent/ /src/pi-nfc-agent/
RUN sh /src/pi-nfc-agent/build-bundle.sh "${VERSION}" /out/pi

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
# tzdata: TimeZoneInfo.FindSystemTimeZoneById(Europe/Berlin) braucht die
# Zonendaten auf Alpine; ENV TZ=Europe/Berlin = Fallback-Zeitzone (Stundenuebersicht
# fragt die User-Zeitzone primaer via /api/users/me ab).
RUN apk add --no-cache tzdata
ENV TZ=Europe/Berlin
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=api-build /app/publish ./
COPY --from=pi-bundle /out/pi ./wwwroot/pi
ENTRYPOINT ["dotnet", "Stempeluhr.Api.dll"]
