#!/usr/bin/env bash
# Print a registry digest; exit 2 only for a confirmed missing manifest.
# All other failures must stop the workflow instead of allowing a tag overwrite.
set -euo pipefail
ref="${1:?image reference required}"
error="$(mktemp)"
trap 'rm -f "$error"' EXIT
if digest="$(docker buildx imagetools inspect "$ref" --format '{{.Manifest.Digest}}' 2>"$error")"; then
  if [[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    printf '%s\n' "$digest"
    exit 0
  fi
  echo "Invalid registry digest for $ref: $digest" >&2
else
  # Match the complete Buildx manifest-not-found diagnostic, not arbitrary
  # occurrences of 'not found' in network, authentication or other errors.
  if grep -Fxq "ERROR: ${ref}: not found" "$error"; then
    exit 2
  fi
  cat "$error" >&2
fi
exit 1
