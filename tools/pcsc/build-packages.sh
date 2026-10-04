#!/usr/bin/env bash
# Run in Debian 13, arm64. Build the upstream release with Debian packaging;
# never add unstable repositories to the terminal itself.
set -euo pipefail
OUT="${1:?output directory required}"
[ "$(dpkg --print-architecture)" = arm64 ] || { echo 'Expected arm64 build environment' >&2; exit 1; }
. /etc/os-release
[ "$VERSION_CODENAME" = trixie ] || { echo 'Expected Debian trixie' >&2; exit 1; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"
SOURCE=https://deb.debian.org/debian/pool/main/p/pcsc-lite
download() {
  if ! curl -fsSL --retry 2 -o "$1" "$SOURCE/$1"; then
    # Debian can remove superseded source packages from the live mirror.
    curl -fsSL --retry 2 -o "$1" "https://snapshot.debian.org/file/$2"
  fi
}
download pcsc-lite_2.5.2.orig.tar.xz 2809fa33fe346ca4db71dd618d5dd92bc873b2ec
download pcsc-lite_2.5.2-1.debian.tar.xz 573626ccaf32df65117ee8aa9b663363c0d2f42d
cat > sources.sha256 <<'EOF'
60a08942d8c00a1d86a3bf4c64eddbf7a5569c7c54f6904e3f07801bb5316acd  pcsc-lite_2.5.2.orig.tar.xz
c6235e2ad6a2469c0e002bf1eabf6c831565862f945b42a1e7065911cb596c2b  pcsc-lite_2.5.2-1.debian.tar.xz
EOF
sha256sum -c sources.sha256
tar -xf pcsc-lite_2.5.2.orig.tar.xz
cd pcsc-lite-2.5.2
tar -xf ../pcsc-lite_2.5.2-1.debian.tar.xz
# Trixie has debhelper 13; the upstream Debian package targets compat 14.
sed -i 's/debhelper-compat (= 14)/debhelper-compat (= 13)/' debian/control
cat > changelog <<'EOF'
pcsc-lite (2.5.2-1~stempeluhr13.1) trixie; urgency=medium

  * Rebuild upstream 2.5.2 (includes Polkit leak fix) for Stempeluhr terminals.
  * Use debhelper compat 13 available in trixie; keep Polkit and libudev.

 -- Stempeluhr maintainers <noreply@stempeluhr.invalid>  Sun, 04 Oct 2026 12:00:00 +0000

EOF
cat debian/changelog >> changelog
mv changelog debian/changelog
export SOURCE_DATE_EPOCH=1791115200
dpkg-buildpackage -b -us -uc
mkdir -p "$OUT"
cp ../pcscd_2.5.2-1~stempeluhr13.1_arm64.deb ../libpcsclite1_2.5.2-1~stempeluhr13.1_arm64.deb \
   ../libpcsclite-dev_2.5.2-1~stempeluhr13.1_arm64.deb ../*.buildinfo ../sources.sha256 "$OUT/"
python3 - "$OUT" <<'PY'
import hashlib, json, pathlib, sys
directory = pathlib.Path(sys.argv[1])
packages = [{"file": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
            for p in sorted(directory.glob("*.deb"))]
(directory / "manifest.json").write_text(json.dumps({"distribution": "trixie", "architecture": "arm64",
    "version": "2.5.2-1~stempeluhr13.1", "upstreamVersion": "2.5.2", "packages": packages}, indent=2) + "\n")
PY
