#!/usr/bin/env python3
"""Read an ACR122U NFC reader via PC/SC and hand scans to the kiosk UI.

The agent is a pure UID bridge: every card scan is published on a loopback
HTTP server (``GET /scan/latest``) and the kiosk web app confirms it via
``POST /scan/ack``. A scan only IDENTIFIES the employee - the actual stamp is
triggered by a button in the kiosk UI, which also owns the offline queue.
Without an ack within ``selection_timeout_seconds`` the scan is dropped.
"""

from __future__ import annotations

import argparse
import http.server
import json
import logging
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from smartcard.Exceptions import CardConnectionException, NoCardException
from smartcard.System import readers


LOGGER = logging.getLogger("stempeluhr-nfc-agent")
GET_UID_APDU = [0xFF, 0xCA, 0x00, 0x00, 0x00]

# Loopback port of the local scan server.
DEFAULT_LOCAL_SCAN_PORT = 8737

# Written next to this script by the Docker build (see Dockerfile, stage
# pi-bundle). The updater compares it with the server version.
VERSION_FILE = Path(__file__).resolve().parent / "VERSION"
DEV_VERSION = "0.0.0-local"


def read_version(path: Path = VERSION_FILE) -> str:
    try:
        return path.read_text(encoding="utf-8").strip() or DEV_VERSION
    except OSError:
        return DEV_VERSION


AGENT_VERSION = read_version()


@dataclass(frozen=True)
class AgentConfig:
    api_base_url: str
    terminal_id: str
    debounce_seconds: float
    reader_name_contains: str | None
    local_port: int = DEFAULT_LOCAL_SCAN_PORT
    # How long the kiosk UI may take to ack a published scan before the scan
    # is dropped.
    selection_timeout_seconds: float = 10.0

    @staticmethod
    def load(path: Path) -> "AgentConfig":
        with path.open("r", encoding="utf-8") as config_file:
            raw: dict[str, Any] = json.load(config_file)

        # api_base_url is only needed by the updater (update.sh), but a
        # missing value means a broken installation - fail loudly.
        api_base_url = str(raw.get("api_base_url", "")).rstrip("/")
        if not api_base_url:
            raise ValueError("api_base_url is required")

        # Keys of older agent versions (reader_token, queue_path,
        # fallback_mode) are ignored on purpose: existing config files keep
        # working after an update.
        return AgentConfig(
            api_base_url=api_base_url,
            terminal_id=str(raw.get("terminal_id") or "default"),
            debounce_seconds=float(raw.get("debounce_seconds") or 3),
            reader_name_contains=raw.get("reader_name_contains"),
            local_port=int(raw.get("local_port") or DEFAULT_LOCAL_SCAN_PORT),
            selection_timeout_seconds=float(
                raw.get("selection_timeout_seconds") or 10
            ),
        )


@dataclass
class LastScan:
    card_id: str
    scanned_at_epoch: float
    consumed: bool = False


class _LocalScanHandler(http.server.BaseHTTPRequestHandler):
    """Serves the last NFC scan to the kiosk UI on the loopback interface."""

    def do_GET(self) -> None:  # noqa: N802 - stdlib naming convention
        scan_server: LocalScanServer = self.server.scan_server  # type: ignore[attr-defined]
        if self.path == "/health":
            self._send_json(200, {"ok": True, "version": AGENT_VERSION})
            return

        if self.path != "/scan/latest":
            self._send_json(404, {"error": "not found"})
            return

        scan = scan_server.latest_scan()
        if scan is None:
            self._send_json(404, {"error": "no scan available"})
            return

        self._send_json(
            200,
            {
                "cardId": scan.card_id,
                "scannedAt": iso8601_from_epoch(scan.scanned_at_epoch),
                "consumed": scan.consumed,
            },
        )

    def do_POST(self) -> None:  # noqa: N802 - stdlib naming convention
        scan_server: LocalScanServer = self.server.scan_server  # type: ignore[attr-defined]
        if self.path != "/scan/ack":
            self._send_json(404, {"error": "not found"})
            return

        if scan_server.ack_latest():
            self._send_json(200, {"ok": True})
        else:
            self._send_json(404, {"error": "no scan available"})

    def do_OPTIONS(self) -> None:  # noqa: N802 - stdlib naming convention
        # CORS preflight: the kiosk UI runs on a different origin (the
        # backend host) than this loopback server, so the browser blocks
        # both the GET and the POST without these headers.
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        # Chrome's Private Network Access: a request from a PUBLIC site
        # (e.g. https://...cloudflare...) to a LOCAL address (127.0.0.1)
        # gets an extended preflight asking for this header - without it
        # the fetch fails even though plain CORS is satisfied.
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.end_headers()

    def _send_json(self, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002
        LOGGER.debug("Local scan server: " + format, *args)


class LocalScanServer:
    """Loopback HTTP server exposing the most recent card scan.

    The Angular UI polls ``GET /scan/latest`` and confirms handling via
    ``POST /scan/ack``. ``GET /health`` reports the agent version for the
    updater. Binds only on 127.0.0.1 so the endpoint is never reachable from
    the network.
    """

    def __init__(self, port: int = DEFAULT_LOCAL_SCAN_PORT) -> None:
        self._scan: LastScan | None = None
        self._lock = threading.Lock()
        outer = self

        class _Server(http.server.ThreadingHTTPServer):
            scan_server = outer

        self._httpd = _Server(("127.0.0.1", port), _LocalScanHandler)
        self._httpd.daemon_threads = True

    @property
    def url(self) -> str:
        """Base URL, resolving port 0 to the actually bound port."""
        return f"http://127.0.0.1:{self._httpd.server_address[1]}"

    def publish_scan(self, card_id: str, scanned_at_epoch: float) -> None:
        """Records a new scan, replacing any previous one."""
        with self._lock:
            self._scan = LastScan(card_id=card_id, scanned_at_epoch=scanned_at_epoch)

    def expire_latest(self) -> None:
        """Marks the current scan as consumed after it timed out.

        Without this a LATE UI ack (tab throttling can delay the poll by
        seconds) would still pick up a scan the agent already reported as
        dropped.
        """
        with self._lock:
            if self._scan is not None:
                self._scan.consumed = True

    def latest_scan(self) -> LastScan | None:
        with self._lock:
            if self._scan is None:
                return None
            # Return a copy so a concurrent ack_latest() cannot mutate the
            # object a caller is currently serializing.
            return LastScan(**vars(self._scan))

    def ack_latest(self) -> bool:
        with self._lock:
            if self._scan is None:
                return False
            self._scan.consumed = True
            return True

    def start_background(self) -> threading.Thread:
        thread = threading.Thread(
            target=self.serve_forever,
            name="local-scan-server",
            daemon=True,
        )
        thread.start()
        return thread

    def serve_forever(self) -> None:
        try:
            self._httpd.serve_forever()
        except Exception:
            LOGGER.exception("Local scan server crashed")

    def shutdown(self) -> None:
        self._httpd.shutdown()

    def server_close(self) -> None:
        self._httpd.server_close()


def main() -> int:
    parser = argparse.ArgumentParser(description="Stempeluhr NFC agent for ACR122U readers")
    parser.add_argument(
        "--config",
        default="/etc/stempeluhr-nfc-agent/config.json",
        help="Path to the JSON configuration file",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )

    config = AgentConfig.load(Path(args.config))
    LOGGER.info(
        "Starting NFC agent %s for terminal '%s'", AGENT_VERSION, config.terminal_id
    )

    try:
        # The bind() in the constructor raises OSError when the port is
        # already taken - fail with a clear log line instead of a traceback.
        scan_server = LocalScanServer(port=config.local_port)
        scan_thread = scan_server.start_background()
    except OSError as error:
        LOGGER.error(
            "Cannot start local scan server on port %d: %s", config.local_port, error
        )
        return 1
    LOGGER.info("Local scan server listening on %s", scan_server.url)

    try:
        run(config, scan_server)
    finally:
        scan_server.shutdown()
        scan_thread.join(timeout=5)
        scan_server.server_close()
    return 0


def run(config: AgentConfig, scan_server: LocalScanServer) -> None:
    last_uid: str | None = None
    last_submit_at = 0.0
    selected_reader_name: str | None = None

    while True:
        try:
            reader = select_reader(config.reader_name_contains)
            if reader is None:
                LOGGER.warning("No PC/SC reader found. Waiting for ACR122U...")
                time.sleep(3)
                continue

            reader_name = str(reader)
            if reader_name != selected_reader_name:
                selected_reader_name = reader_name
                LOGGER.info("Using PC/SC reader: %s", reader_name)

            uid = read_uid(reader)
            if uid is None:
                time.sleep(0.2)
                continue

            now = time.monotonic()
            if uid == last_uid and now - last_submit_at < config.debounce_seconds:
                time.sleep(0.2)
                continue

            # Book the debounce and wait for the card to leave even if no
            # ack arrives - another tap of the same card must be a new scan.
            try:
                handle_card_scan(config, uid, scan_server)
            finally:
                last_uid = uid
                last_submit_at = now
                wait_until_card_removed(reader)
        except KeyboardInterrupt:
            raise
        except Exception:
            LOGGER.exception("Unexpected NFC loop error")
            time.sleep(2)


def beep(status: str) -> None:
    """Audible feedback via terminal bell.

    There is no dedicated buzzer wired up yet; the bell character goes to
    stderr so a terminal/kiosk shell gives at least a signal. ``status`` is
    "ok" for a handled scan, "error" for a dropped one.
    """
    bell = "\a" if status == "ok" else "\a\a\a"
    sys.stderr.write(bell)
    sys.stderr.flush()


def handle_card_scan(
    config: AgentConfig,
    card_id: str,
    scan_server: LocalScanServer,
    selection_timeout: float | None = None,
) -> str:
    """Publishes the scan to the kiosk UI and waits for its ack.

    Returns "acked", "superseded" (a newer scan replaced this one) or
    "dropped" (no ack within the selection timeout).
    """
    scanned_at = utc_now_epoch()
    timeout = (
        selection_timeout
        if selection_timeout is not None
        else config.selection_timeout_seconds
    )

    # publish_scan replaces any previous scan: the watchdog below only ever
    # judges the newest one.
    scan_server.publish_scan(card_id, scanned_at)

    outcome = _wait_for_ack(scan_server, card_id, scanned_at, timeout)
    if outcome == "timeout":
        # Late-ack race: the ack may have landed just after _wait_for_ack
        # gave up. Re-check once; if consumed meanwhile, treat like "acked".
        scan = scan_server.latest_scan()
        if scan is not None and scan.card_id == card_id and scan.consumed:
            outcome = "acked"

    if outcome == "acked":
        beep("ok")
        LOGGER.info("Card %s published and acked by UI.", card_id)
        return "acked"
    if outcome == "superseded":
        return "superseded"

    beep("error")
    LOGGER.info(
        "Card %s not acked within %.1fs - scan dropped. Is the kiosk page open?",
        card_id,
        timeout,
    )
    # Expire it so a LATE UI ack cannot pick up a scan that was already
    # reported as dropped.
    scan_server.expire_latest()
    return "dropped"


def _wait_for_ack(
    scan_server: LocalScanServer,
    card_id: str,
    scanned_at: float,
    timeout: float,
) -> str:
    """Waits until the published scan is consumed by the UI.

    Returns "acked" on ack, "superseded" when a newer scan replaced ours
    (the newer watchdog takes over) and "timeout" once the selection timeout
    elapsed without an ack.
    """
    deadline = time.monotonic() + max(0.0, timeout)
    while time.monotonic() < deadline:
        scan = scan_server.latest_scan()
        if scan is None or scan.card_id != card_id or scan.scanned_at_epoch != scanned_at:
            return "superseded"
        if scan.consumed:
            return "acked"
        time.sleep(0.05)
    return "timeout"


def utc_now_epoch() -> float:
    return time.time()


def iso8601_from_epoch(epoch_seconds: float) -> str:
    """Converts an epoch timestamp to an ISO-8601 string (UTC)."""
    return datetime.fromtimestamp(epoch_seconds, tz=timezone.utc).isoformat()


def select_reader(name_filter: str | None):
    available_readers = readers()
    if not available_readers:
        return None

    if not name_filter:
        return available_readers[0]

    lowered_filter = name_filter.lower()
    for reader in available_readers:
        if lowered_filter in str(reader).lower():
            return reader

    LOGGER.warning("No reader matching '%s'. Available readers: %s", name_filter, available_readers)
    return None


def read_uid(reader) -> str | None:
    try:
        connection = reader.createConnection()
        connection.connect()
        data, sw1, sw2 = connection.transmit(GET_UID_APDU)
    except (CardConnectionException, NoCardException):
        return None

    if (sw1, sw2) != (0x90, 0x00):
        LOGGER.warning("Reader returned unexpected status %02X %02X", sw1, sw2)
        return None

    return "".join(f"{byte:02X}" for byte in data)


def wait_until_card_removed(reader) -> None:
    while read_uid(reader) is not None:
        time.sleep(0.2)


if __name__ == "__main__":
    sys.exit(main())
