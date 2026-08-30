#!/usr/bin/env bash
# Kiosk-Cache-Reset für den Stempeluhr-Pi (remote via SSH/Tailscale als root ausführen).
#
# Wann nötig: Der Chromium-Kiosk hat die alte Angular-App aus dem Disk-Cache
# geladen (z.B. nach einem Update VOR der Auto-Reload/No-Cache-Header-Ära,
# oder als manueller Fallback). Symptom: Agent liest die Karte, aber im Log
# steht "Card <UID> not acked within 10.0s ... scan dropped" und am Kiosk
# erscheint kein Name. Ein Pi-Reboot allein hilft NICHT - der Cache überlebt.
#
# Seit den SPA-Cache-Headern + Auto-Reload (PR #24) ist das nur noch als
# manueller Fallback nötig (z.B. hängengebliebener Kiosk).
#
# Verifikation nach dem Reboot: journalctl -u stempeluhr-nfc-agent -n 20
# Beim nächsten echten Scan muss "Card ... published and acked by UI." stehen.
set -euo pipefail

# Kiosk-Benutzer aus dem laufenden Chromium-Prozess ermitteln, Fallback: stempeluhradmin
KIOSK_USER="$(ps -eo user:32,args | awk '/chromium/ && /--kiosk/ && !/awk/ {print $1; exit}')"
KIOSK_USER="${KIOSK_USER:-stempeluhradmin}"

echo "==> Stoppe Chromium (Kiosk-User: ${KIOSK_USER})"
pkill -f 'chromium' || true
sleep 3

PROFILE="/home/${KIOSK_USER}/.config/chromium/Default"
if [ ! -d "${PROFILE}" ]; then
  echo "FEHLER: Chromium-Profil nicht gefunden: ${PROFILE}" >&2
  exit 1
fi

echo "==> Lösche HTTP-, JS- und Service-Worker-Cache"
rm -rf "${PROFILE}/Cache" "${PROFILE}/Code Cache" "${PROFILE}/Service Worker"

echo "==> Pi neu starten (Autostart zieht den Browser mit leerem Cache hoch)"
reboot
