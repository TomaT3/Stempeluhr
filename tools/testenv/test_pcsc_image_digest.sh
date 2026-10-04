#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/docker" <<'SH'
#!/usr/bin/env bash
case "$LOOKUP_CASE" in
  exists) printf 'sha256:%064d\n' 1 ;;
  missing) echo "ERROR: $4: not found" >&2; exit 1 ;;
  network) echo 'ERROR: failed to do request: TLS handshake timeout' >&2; exit 1 ;;
  auth) echo 'ERROR: denied: requested access to the resource is denied' >&2; exit 1 ;;
  other-not-found) echo 'ERROR: credential helper not found' >&2; exit 1 ;;
  invalid) echo '<nil>' ;;
esac
SH
chmod +x "$WORK/docker"
export PATH="$WORK:$PATH"
ref=ghcr.io/tomat3/stempeluhr/pcsc-packages:inputs-test
for scenario in exists missing network auth other-not-found invalid; do
  export LOOKUP_CASE="$scenario"
  status=0
  bash "$ROOT/tools/pcsc/image-digest.sh" "$ref" > "$WORK/out" 2> "$WORK/err" || status=$?
  case "$scenario" in
    exists)
      [ "$status" -eq 0 ]
      [ "$(cat "$WORK/out")" = "$(printf 'sha256:%064d' 1)" ] ;;
    missing) [ "$status" -eq 2 ]; [ ! -s "$WORK/out" ] ;;
    *) [ "$status" -eq 1 ]; [ ! -s "$WORK/out" ]; [ -s "$WORK/err" ] ;;
  esac
  echo "Registry lookup: $scenario passed"
done
