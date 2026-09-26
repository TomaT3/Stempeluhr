#!/usr/bin/env bash
# Richtet einen Raspberry Pi als Stempeluhr-Terminal ein (oder stellt einen
# bestehenden auf das automatische Agent-Update um). Idempotent: ein zweiter
# Lauf ändert nur, was fehlt.
#
# Neues Terminal:
#   curl -fsSL https://<host>/pi/install.sh | sudo bash -s -- \
#     --server https://<host> --terminal-id stempeluhr-pi-02 --kiosk-user kiosk
#
# Bestehendes Terminal (config.json vorhanden, Werte werden daraus gelesen):
#   curl -fsSL https://<host>/pi/install.sh | sudo bash -s --
#
# Optionen:
#   --server URL          Basis-URL der Stempeluhr (ohne /terminal)
#   --terminal-id ID      Terminal-Kennung (= terminalId der Kiosk-URL)
#   --reader NAME         Filter auf den PC/SC-Reader-Namen (Default: ACR122)
#   --kiosk-user USER     Chromium-Autostart für diesen Benutzer anlegen
#   --skip-apt            keine Pakete installieren (Tests)
#
# Nicht enthalten (siehe docs/raspberry-pi-kiosk-nfc.md): Desktop-Autologin,
# WLAN-Power-Save, unattended-upgrades.
set -euo pipefail

CONFIG_DIR=/etc/stempeluhr-nfc-agent
CONFIG="$CONFIG_DIR/config.json"
AGENT_DIR=/opt/stempeluhr-nfc-agent
SERVICE_USER=stempeluhr

SERVER=""
TERMINAL_ID=""
READER="ACR122"
KIOSK_USER=""
SKIP_APT=0

usage() { sed -n '2,22p' "$0" 2>/dev/null || true; exit 1; }
log() { echo "==> $*"; }
fail() { echo "FEHLER: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --server) SERVER="${2:-}"; shift 2 ;;
    --terminal-id) TERMINAL_ID="${2:-}"; shift 2 ;;
    --reader) READER="${2:-}"; shift 2 ;;
    --kiosk-user) KIOSK_USER="${2:-}"; shift 2 ;;
    --skip-apt) SKIP_APT=1; shift ;;
    -h | --help) usage ;;
    *) fail "Unbekannte Option: $1" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || fail "bitte mit sudo ausführen"

if [ "$SKIP_APT" -eq 0 ]; then
  log "Pakete installieren (pcscd, pyscard, curl)"
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq pcscd pcsc-tools python3-pyscard curl
  systemctl enable --now pcscd
fi

config_value() { # schluessel
  python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get(sys.argv[2]) or "")' "$CONFIG" "$1" 2>/dev/null || true
}

if [ -f "$CONFIG" ]; then
  SERVER="${SERVER:-$(config_value api_base_url)}"
  TERMINAL_ID="${TERMINAL_ID:-$(config_value terminal_id)}"
fi
SERVER="${SERVER%/}"
[ -n "$SERVER" ] || fail "--server fehlt (und keine bestehende $CONFIG)"
[ -n "$TERMINAL_ID" ] || fail "--terminal-id fehlt (und keine bestehende $CONFIG)"
[[ "$SERVER" =~ ^https?://[^/]+$ ]] || fail "--server muss eine Basis-URL wie https://stempeluhr.example.com sein"

log "Service-Benutzer '$SERVICE_USER' und PC/SC-Zugriff"
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home /nonexistent --shell /usr/sbin/nologin "$SERVICE_USER"
fi
POLKIT_RULE=/etc/polkit-1/rules.d/50-stempeluhr-pcsc.rules
POLKIT_CONTENT='polkit.addRule(function(action, subject) {
    if ((action.id == "org.debian.pcsc-lite.access_pcsc" ||
         action.id == "org.debian.pcsc-lite.access_card") &&
        subject.user == "stempeluhr") {
        return polkit.Result.YES;
    }
});'
if [ "$(cat "$POLKIT_RULE" 2>/dev/null)" != "$POLKIT_CONTENT" ]; then
  mkdir -p "$(dirname "$POLKIT_RULE")"
  printf '%s\n' "$POLKIT_CONTENT" > "$POLKIT_RULE"
  systemctl restart polkit 2>/dev/null || true
  systemctl restart pcscd 2>/dev/null || true
fi

if [ ! -f "$CONFIG" ]; then
  log "Konfiguration $CONFIG anlegen"
  mkdir -p "$CONFIG_DIR"
  python3 - "$CONFIG" "$SERVER" "$TERMINAL_ID" "$READER" <<'PY'
import json, sys
path, server, terminal_id, reader = sys.argv[1:5]
config = {
    "api_base_url": server,
    "terminal_id": terminal_id,
    "debounce_seconds": 3,
    "reader_name_contains": reader,
    "local_port": 8737,
    "selection_timeout_seconds": 10,
}
with open(path, "w", encoding="utf-8") as handle:
    json.dump(config, handle, indent=2)
    handle.write("\n")
PY
fi
chown root:"$SERVICE_USER" "$CONFIG"
chmod 640 "$CONFIG"

log "Agent von $SERVER installieren"
mkdir -p "$AGENT_DIR"
UPDATER="$(mktemp)"
trap 'rm -f "$UPDATER"' EXIT
curl -fsS --max-time 30 -o "$UPDATER" "$SERVER/pi/update.sh" || fail "$SERVER/pi/update.sh nicht erreichbar"
bash "$UPDATER" --force
[ -f "$AGENT_DIR/current/VERSION" ] || fail "$SERVER liefert kein Agent-Bundle (/pi/agent.json)"

# Dateien des alten, manuell kopierten Agenten (vor dem Release-Layout).
rm -f "$AGENT_DIR"/*.py "$AGENT_DIR"/*.py.bak-*

systemctl enable stempeluhr-nfc-agent.service
systemctl enable --now stempeluhr-nfc-agent-update.timer

# Chromium ab Version 142 blockiert Anfragen einer öffentlichen Seite an
# 127.0.0.1 (Local Network Access), bis jemand einen Erlaubnis-Dialog
# bestätigt - im Kiosk legt das den Kartenleser still. Die Policy gibt die
# Stempeluhr-Origin frei; ab Chromium 145 heißt die Loopback-Freigabe
# LoopbackNetworkAllowedForUrls, ältere Versionen ignorieren unbekannte
# Einträge. Wirkt beim nächsten Chromium-Start.
log "Chromium-Policy: lokalen Agenten für $SERVER erlauben"
mkdir -p /etc/chromium/policies/managed
printf '{\n  "LocalNetworkAccessAllowedForUrls": ["%s"],\n  "LoopbackNetworkAllowedForUrls": ["%s"]\n}\n' "$SERVER" "$SERVER" \
  > /etc/chromium/policies/managed/stempeluhr.json

if [ -n "$KIOSK_USER" ]; then
  id "$KIOSK_USER" >/dev/null 2>&1 || fail "Kiosk-Benutzer '$KIOSK_USER' existiert nicht"
  KIOSK_HOME="$(getent passwd "$KIOSK_USER" | cut -d: -f6)"
  KIOSK_URL="$SERVER/terminal?terminalId=$TERMINAL_ID"

  log "Chromium-Autostart für '$KIOSK_USER' -> $KIOSK_URL"
  install -d -o "$KIOSK_USER" -g "$KIOSK_USER" "$KIOSK_HOME/.config" "$KIOSK_HOME/.config/autostart"
  cat > "$KIOSK_HOME/.config/autostart/stempeluhr-kiosk.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Stempeluhr Kiosk
Exec=chromium --password-store=basic --no-first-run --no-default-browser-check --kiosk --noerrdialogs --disable-infobars --disable-session-crashed-bubble --app=$KIOSK_URL
X-GNOME-Autostart-enabled=true
EOF
  chown "$KIOSK_USER:$KIOSK_USER" "$KIOSK_HOME/.config/autostart/stempeluhr-kiosk.desktop"
fi

log "Fertig. Terminal '$TERMINAL_ID' holt Agent-Updates jetzt selbst von $SERVER."
echo "    Status:  systemctl status stempeluhr-nfc-agent stempeluhr-nfc-agent-update.timer"
echo "    Version: curl -s http://127.0.0.1:8737/health"
