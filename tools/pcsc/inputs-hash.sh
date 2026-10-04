#!/usr/bin/env bash
# Content hash of the PC/SC package build inputs. It ends the published image
# tag, so the pin in the main Dockerfile shows which inputs it was built from.
set -euo pipefail
cd "$(dirname "$0")"
cat Dockerfile build-packages.sh | tr -d '\r' | sha256sum | cut -c1-12
