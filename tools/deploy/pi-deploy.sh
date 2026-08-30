#!/usr/bin/env bash
# Zentrales Deploy für Stempeluhr-Pi-Clients (Agent-Dateien + Kiosk-Cache-Reset).
#
# Voraussetzungen auf dem Admin-Rechner:
#   - gh CLI, authentifiziert (Token mit Repo-Zugriff) ODER curl
#     (Fallback: lädt das Release-Asset direkt - funktioniert, weil das Repo
#     public ist; in Git Bash unter Windows ist curl immer vorhanden)
#   - SSH-Zugang zu allen Pis (Tailscale), User mit passwordless sudo
#   - tools/deploy/pis.conf (Kopie von pis.conf.example) mit einem Host pro Zeile
#
# Verwendung:
#   ./pi-deploy.sh agent v0.8.0   # Agent-Dateien (aus Release-ZIP) auf alle Pis
#   ./pi-deploy.sh kiosk          # Kiosk-Chromium-Cache-Reset auf allen Pis (nach Server-Deploy)
#   ./pi-deploy.sh all v0.8.0     # beides
#
# Der Agent wird NIE verändert: /etc/stempeluhr-nfc-agent/config.json bleibt
# unangetastet; die .py-Dateien werden mit Zeitstempel-Backup ersetzt.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PIS_CONF="${PIS_CONF:-$SCRIPT_DIR/pis.conf}"

# Aufräumen am Skript-Ende (auch bei exit 1): EXIT statt RETURN, damit der
# tmp-Ordner nicht liegen bleibt, wenn deploy_agent vorzeitig endet.
TMP_ZIP_DIR=""
trap 'if [ -n "$TMP_ZIP_DIR" ]; then rm -rf "$TMP_ZIP_DIR"; fi' EXIT

usage() {
  echo "Verwendung: $0 {agent <version> | kiosk | all <version>}" >&2
  echo "  agent vX.Y.Z  Agent-Dateien aus dem Release-ZIP deployen" >&2
  echo "  kiosk         Kiosk-Chromium-Cache-Reset (nach Server-Deploy)" >&2
  echo "  all vX.Y.Z    beides" >&2
  exit 1
}

CMD="${1:-}"
VERSION="${2:-}"
case "$CMD" in
  agent | all) [ -n "$VERSION" ] || usage ;;
  kiosk) ;;
  *) usage ;;
esac

[ -f "$PIS_CONF" ] || {
  echo "FEHLER: $PIS_CONF fehlt - Kopie von pis.conf.example anlegen (user@tailscale-host pro Zeile)." >&2
  exit 1
}

mapfile -t HOSTS < <(grep -vE '^\s*(#|$)' "$PIS_CONF")
[ "${#HOSTS[@]}" -gt 0 ] || { echo "FEHLER: keine Hosts in $PIS_CONF." >&2; exit 1; }

REMOTE_SCRIPT=$(cat <<'REMOTE_EOF'
set -euo pipefail
AGENT_DIR=/opt/stempeluhr-nfc-agent
TS="$(date +%Y%m%d-%H%M%S)"

echo "  Backup bisheriger Dateien (Zeitstempel-Suffix .bak-$TS)..."
for f in stempeluhr_nfc_agent.py offline_queue.py; do
  if [ -f "$AGENT_DIR/$f" ]; then
    sudo cp "$AGENT_DIR/$f" "$AGENT_DIR/$f.bak-$TS"
  fi
done

echo "  Entpacke ZIP und spiele Dateien ein..."
sudo rm -rf /tmp/pi-nfc-agent-extract
sudo mkdir -p /tmp/pi-nfc-agent-extract
sudo unzip -o -q /tmp/pi-nfc-agent.zip -d /tmp/pi-nfc-agent-extract
sudo cp /tmp/pi-nfc-agent-extract/stempeluhr_nfc_agent.py \
        /tmp/pi-nfc-agent-extract/offline_queue.py \
        "$AGENT_DIR/"
sudo chown root:root "$AGENT_DIR/stempeluhr_nfc_agent.py" "$AGENT_DIR/offline_queue.py"
sudo rm -rf /tmp/pi-nfc-agent-extract /tmp/pi-nfc-agent.zip

echo "  Service neu starten..."
sudo systemctl restart stempeluhr-nfc-agent

echo "  Health-Check (warte bis zu 15 s auf aktiven Service)..."
ACTIVE=""
for _ in $(seq 1 15); do
  sleep 1
  if systemctl is-active --quiet stempeluhr-nfc-agent; then
    ACTIVE="yes"
    break
  fi
done

if [ "$ACTIVE" != "yes" ]; then
  echo "  ✗ Service wurde nicht aktiv - Rollback auf Backup (.bak-$TS)..." >&2
  for f in stempeluhr_nfc_agent.py offline_queue.py; do
    if [ -f "$AGENT_DIR/$f.bak-$TS" ]; then
      sudo cp "$AGENT_DIR/$f.bak-$TS" "$AGENT_DIR/$f"
    fi
  done
  sudo chown root:root "$AGENT_DIR/stempeluhr_nfc_agent.py" "$AGENT_DIR/offline_queue.py"
  sudo systemctl restart stempeluhr-nfc-agent

  # Rollback-Folge-Check mit Retry (bis 10 s): bei trägem Start (pyscard-Init)
  # nicht sofort eine falsch-negative Warnung melden.
  ROLLBACK_ACTIVE=""
  for _ in $(seq 1 10); do
    sleep 1
    if systemctl is-active --quiet stempeluhr-nfc-agent; then
      ROLLBACK_ACTIVE="yes"
      break
    fi
  done
  if [ "$ROLLBACK_ACTIVE" != "yes" ]; then
    echo "  ✗ AUCH Rollback-Version nicht aktiv - manuelles Eingreifen nötig!" >&2
    exit 1
  fi
  echo "  ↻ Rollback abgeschlossen, vorherige Version läuft wieder"
  exit 1
fi

echo "  ✓ Service aktiv"
journalctl -u stempeluhr-nfc-agent -n 10 --no-pager | grep -E "Local scan server listening|Using PC/SC reader" || true
REMOTE_EOF
)

deploy_agent() {
  local version="$1" zipfile host
  TMP_ZIP_DIR="$(mktemp -d)"

  echo "==> Lade pi-nfc-agent-${version}.zip von GitHub (Release-Asset)..."
  if command -v gh &>/dev/null; then
    gh release download "$version" --pattern "pi-nfc-agent-*.zip" --dir "$TMP_ZIP_DIR" >/dev/null
  else
    # Fallback ohne gh (z. B. Git Bash unter Windows): das Repo ist public,
    # das Asset liegt unter der festen Release-Download-URL. gh bleibt
    # bevorzugt, weil es die Asset-Liste auflöst statt den Namen zu raten.
    curl -fsSL -o "$TMP_ZIP_DIR/pi-nfc-agent-$version.zip" \
      "https://github.com/TomaT3/Stempeluhr/releases/download/$version/pi-nfc-agent-$version.zip"
  fi
  zipfile="$(ls "$TMP_ZIP_DIR"/pi-nfc-agent-*.zip 2>/dev/null | head -1 || true)"
  [ -n "$zipfile" ] || { echo "FEHLER: kein pi-nfc-agent-*.zip in Release $version gefunden." >&2; exit 1; }

  for host in "${HOSTS[@]}"; do
    echo "==> [$host] Agent-Update auf $version"
    # ConnectTimeout: offline Pis (Tailscale antwortet nicht) scheitern nach
    # 10 s statt nach TCP-Timeout; BatchMode: keine Passwort-Prompts.
    if ! scp -q -o ConnectTimeout=10 -o BatchMode=yes "$zipfile" "$host:/tmp/pi-nfc-agent.zip"; then
      echo "  ✗ scp fehlgeschlagen (Tailscale/SSH ok?)" >&2
      continue
    fi
    if ssh -o ConnectTimeout=10 -o BatchMode=yes "$host" "sudo bash -s" <<< "$REMOTE_SCRIPT"; then
      echo "  ✓ Agent aktualisiert"
    else
      echo "  ✗ Agent-Update fehlgeschlagen" >&2
    fi
  done
}

reset_kiosk() {
  local host
  for host in "${HOSTS[@]}"; do
    echo "==> [$host] Kiosk-Cache-Reset"
    if ! scp -q -o ConnectTimeout=10 -o BatchMode=yes "$SCRIPT_DIR/kiosk-cache-reset.sh" "$host:/tmp/kiosk-cache-reset.sh"; then
      echo "  ✗ scp fehlgeschlagen" >&2
      continue
    fi
    # reboot via SSH beendet die Verbindung -> exit != 0 ist erwartet.
    if ssh -o ConnectTimeout=10 -o BatchMode=yes "$host" "sudo bash /tmp/kiosk-cache-reset.sh"; then
      echo "  ✓ Cache-Reset ausgeführt"
    else
      echo "  → Cache-Reset gestartet (Pi bootet neu; SSH-Abbruch ist normal)"
    fi
  done
}

case "$CMD" in
  agent) deploy_agent "$VERSION" ;;
  kiosk) reset_kiosk ;;
  all)
    deploy_agent "$VERSION"
    reset_kiosk
    ;;
esac

echo "==> Fertig. Verifikation am Kiosk: Badge zeigt neue Version; Agent-Log: 'published and acked by UI'."
