#!/usr/bin/env bash
# Wartung aller Stempeluhr-Pis per SSH (Tailscale).
#
# Agent-Updates laufen seit dem Auto-Update ohne dieses Skript: jeder Pi holt
# sich den Agenten passend zur Server-Version selbst (update.sh per Timer).
# Dieses Skript wird nur noch gebraucht für
#   bootstrap  einmalige Umstellung eines Pis mit manuell kopiertem Agenten
#              auf das Auto-Update (führt /pi/install.sh des Servers aus,
#              config.json bleibt erhalten)
#   status     Agent-Version und Timer-Status aller Pis anzeigen
#   kiosk      Notfall: Chromium-Cache inkl. Service Worker löschen + Reboot
#
# Voraussetzungen auf dem Admin-Rechner:
#   - SSH-Zugang zu allen Pis (Tailscale), User mit passwordless sudo
#   - tools/deploy/pis.conf (Kopie von pis.conf.example) mit einem Host pro Zeile
#
# Verwendung:
#   ./pi-deploy.sh bootstrap
#   ./pi-deploy.sh status
#   ./pi-deploy.sh kiosk
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PIS_CONF="${PIS_CONF:-$SCRIPT_DIR/pis.conf}"
SSH_OPTS=(-o ConnectTimeout=10 -o BatchMode=yes)

usage() {
  echo "Verwendung: $0 {bootstrap | status | kiosk}" >&2
  exit 1
}

CMD="${1:-}"
case "$CMD" in
  bootstrap | status | kiosk) ;;
  *) usage ;;
esac

[ -f "$PIS_CONF" ] || {
  echo "FEHLER: $PIS_CONF fehlt - Kopie von pis.conf.example anlegen (user@tailscale-host pro Zeile)." >&2
  exit 1
}

mapfile -t HOSTS < <(grep -vE '^\s*(#|$)' "$PIS_CONF")
[ "${#HOSTS[@]}" -gt 0 ] || { echo "FEHLER: keine Hosts in $PIS_CONF." >&2; exit 1; }

# Liest die Server-URL aus der vorhandenen Agent-Konfiguration des Pis und
# führt den Installer dieses Servers aus.
BOOTSTRAP_SCRIPT=$(cat <<'REMOTE_EOF'
set -euo pipefail
SERVER="$(sudo python3 -c 'import json; print(json.load(open("/etc/stempeluhr-nfc-agent/config.json"))["api_base_url"].rstrip("/"))')"
echo "  Server: $SERVER"
curl -fsSL "$SERVER/pi/install.sh" | sudo bash -s --
REMOTE_EOF
)

STATUS_SCRIPT=$(cat <<'REMOTE_EOF'
echo "  Agent:  $(curl -s --max-time 3 http://127.0.0.1:8737/health || echo 'nicht erreichbar')"
echo "  Timer:  $(systemctl is-active stempeluhr-nfc-agent-update.timer 2>/dev/null || true)"
REMOTE_EOF
)

run_on_all() { # beschreibung remote-skript
  local host
  for host in "${HOSTS[@]}"; do
    echo "==> [$host] $1"
    # CRLF-Schutz: unter Windows können Zeilenenden \r enthalten.
    if ssh "${SSH_OPTS[@]}" "$host" "bash -s" <<< "${2//$'\r'/}"; then
      echo "  ✓ fertig"
    else
      echo "  ✗ fehlgeschlagen (Tailscale/SSH ok?)" >&2
    fi
  done
}

reset_kiosk() {
  local host
  for host in "${HOSTS[@]}"; do
    echo "==> [$host] Kiosk-Cache-Reset"
    if ! scp -q "${SSH_OPTS[@]}" "$SCRIPT_DIR/kiosk-cache-reset.sh" "$host:/tmp/kiosk-cache-reset.sh"; then
      echo "  ✗ scp fehlgeschlagen" >&2
      continue
    fi
    # CRLF-Schutz: auf Windows ausgecheckte .sh-Dateien können CR-Zeilenenden
    # haben, die Linux-bash als Teil der Befehle sieht (z.B. 'pipefail\r').
    if ssh "${SSH_OPTS[@]}" "$host" "sed -i 's/\r$//' /tmp/kiosk-cache-reset.sh && sudo bash /tmp/kiosk-cache-reset.sh"; then
      echo "  ✓ Cache-Reset ausgeführt"
    else
      echo "  → Cache-Reset gestartet (Pi bootet neu; SSH-Abbruch ist normal)"
    fi
  done
}

case "$CMD" in
  bootstrap) run_on_all "Umstellung auf Auto-Update" "$BOOTSTRAP_SCRIPT" ;;
  status) run_on_all "Status" "$STATUS_SCRIPT" ;;
  kiosk) reset_kiosk ;;
esac
