#!/bin/sh
# Baut das Agent-Bundle, das der Server unter /pi/ ausliefert:
#   agent-<version>.tar.gz  Agent, Units, update.sh, install.sh, VERSION
#   agent.json              {"version", "file", "sha256"} - von update.sh gelesen
#   install.sh, update.sh   für die Ersteinrichtung per curl
#
# Verwendung: build-bundle.sh <version> <ausgabeverzeichnis>
# Läuft im Dockerfile (Alpine/busybox) und in tools/testenv/test_pi_update.sh.
set -eu

VERSION="$1"
OUT="$2"
SRC="$(cd "$(dirname "$0")" && pwd)"

case "$VERSION" in
  *[!0-9A-Za-z.+-]* | "") echo "Ungueltige Version: $VERSION" >&2; exit 1 ;;
esac

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

for f in stempeluhr_nfc_agent.py update.sh install.sh config.example.json \
  stempeluhr-nfc-agent.service stempeluhr-nfc-agent-update.service \
  stempeluhr-nfc-agent-update.timer; do
  cp "$SRC/$f" "$STAGE/$f"
done
printf '%s\n' "$VERSION" > "$STAGE/VERSION"

mkdir -p "$OUT"
FILE="agent-$VERSION.tar.gz"
tar -czf "$OUT/$FILE" -C "$STAGE" .
SHA="$(sha256sum "$OUT/$FILE" | cut -d ' ' -f 1)"
printf '{"version":"%s","file":"%s","sha256":"%s"}\n' "$VERSION" "$FILE" "$SHA" > "$OUT/agent.json"
cp "$SRC/install.sh" "$SRC/update.sh" "$OUT/"
