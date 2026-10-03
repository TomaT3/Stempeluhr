#!/usr/bin/env bash
# Read-only incident export. Run as root on a Pi, redirect stdout on the
# admin computer. Never reads config.json, Chromium profiles or credentials.
set -uo pipefail
SINCE="${1:-24 hours ago}"
section() { printf '\n### %s\n' "$1"; }
section "Time and boots"
date -Is
uptime
journalctl --list-boots --no-pager
section "Versions and agent snapshot"
curl -fsS --max-time 3 http://127.0.0.1:8737/health || true
curl -fsS --max-time 3 http://127.0.0.1:8737/diagnostics || true
chromium --version || true
section "System resources"
free -m
df -h /
vcgencmd measure_temp || true
vcgencmd get_throttled || true
ps -eo pid,comm,pcpu,rss --sort=-rss | head -n 25
section "Services"
systemctl --failed --no-pager
section "Agent journal (may contain card IDs from older agents)"
journalctl -u stempeluhr-nfc-agent -u stempeluhr-nfc-agent-update --since "$SINCE" --no-pager -a
section "Kernel journal"
journalctl -k --since "$SINCE" --no-pager
section "Previous boot kernel"
journalctl -k -b -1 --no-pager || true
section "Bounded diagnostic history (rotations, then current)"
for file in /var/lib/stempeluhr-nfc-agent/diagnostics.jsonl.2 \
            /var/lib/stempeluhr-nfc-agent/diagnostics.jsonl.1 \
            /var/lib/stempeluhr-nfc-agent/diagnostics.jsonl; do
  if [ -f "$file" ]; then
    section "$file"
    cat "$file"
  fi
done
