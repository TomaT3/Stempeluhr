#!/usr/bin/env python3
"""Enumerate a usable configured PC/SC reader without reading a card UID."""
import sys
from smartcard.System import readers

name_filter = sys.argv[1].lower() if len(sys.argv) > 1 else ""
if not any(name_filter in str(reader).lower() for reader in readers()):
    raise SystemExit("Kein passender PC/SC-Leser erkannt")
print("PC/SC-Leser erkannt (kein Karten-Scan ausgeführt)")
