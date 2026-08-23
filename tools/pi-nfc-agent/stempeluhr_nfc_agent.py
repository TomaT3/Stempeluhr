#!/usr/bin/env python3
"""Read an ACR122U NFC reader via PC/SC and submit cards to Stempeluhr.

Offline support: every card scan is appended to a persistent queue before it is
submitted. If the Stempeluhr API (or the internet) is unreachable, the event
stays in the queue and a background retry loop drains it once connectivity
returns. Timestamps are captured at scan time, so the server can replay the
events with their original times.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from smartcard.Exceptions import CardConnectionException, NoCardException
from smartcard.System import readers

from offline_queue import OfflineQueue, QueuedEvent, utc_now_epoch


LOGGER = logging.getLogger("stempeluhr-nfc-agent")
GET_UID_APDU = [0xFF, 0xCA, 0x00, 0x00, 0x00]

# Retry cadence while offline: fast at first, then capped.
RETRY_DELAYS_SECONDS = [5, 10, 30, 60, 120, 300]

# Known clock states reported by the API for local status feedback.
STATE_CLOCKED_IN = "clocked_in"
STATE_PAUSED = "paused"
STATE_CLOCKED_OUT = "clocked_out"


@dataclass(frozen=True)
class AgentConfig:
    api_base_url: str
    terminal_id: str
    reader_token: str | None
    debounce_seconds: float
    reader_name_contains: str | None
    queue_path: Path

    @staticmethod
    def load(path: Path) -> "AgentConfig":
        with path.open("r", encoding="utf-8") as config_file:
            raw: dict[str, Any] = json.load(config_file)

        api_base_url = str(raw.get("api_base_url", "")).rstrip("/")
        if not api_base_url:
            raise ValueError("api_base_url is required")

        reader_token = raw.get("reader_token")
        return AgentConfig(
            api_base_url=api_base_url,
            terminal_id=str(raw.get("terminal_id") or "default"),
            reader_token=str(reader_token) if reader_token else None,
            debounce_seconds=float(raw.get("debounce_seconds") or 3),
            reader_name_contains=raw.get("reader_name_contains"),
            queue_path=Path(
                raw.get("queue_path")
                or "/var/lib/stempeluhr-nfc-agent/offline-queue.json"
            ),
        )


@dataclass
class CardStatusCache:
    """Remembers the last known clock state per card so scans toggle correctly
    even while offline. Access is guarded by a lock because the main scan loop
    and the background retry thread both update the cache."""

    path: Path | None
    _states: dict[str, str] = field(default_factory=dict)
    _lock: threading.Lock = field(default_factory=threading.Lock)

    @staticmethod
    def load(path: Path | None) -> "CardStatusCache":
        cache = CardStatusCache(path=path)
        if path is not None and path.exists():
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(raw, dict):
                    cache._states = {str(k): str(v) for k, v in raw.items()}
            except (json.JSONDecodeError, ValueError):
                pass
        return cache

    def get(self, card_id: str) -> str | None:
        with self._lock:
            return self._states.get(card_id)

    def update(self, card_id: str, state: str) -> None:
        with self._lock:
            self._states[card_id] = state
            if self.path is not None:
                try:
                    self.path.parent.mkdir(parents=True, exist_ok=True)
                    self.path.write_text(
                        json.dumps(self._states, ensure_ascii=False, indent=1),
                        encoding="utf-8",
                    )
                except OSError:
                    pass


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
    LOGGER.info("Starting NFC agent for terminal '%s'", config.terminal_id)

    queue = OfflineQueue.load(config.queue_path)
    if len(queue) > 0:
        LOGGER.info("Restored %d queued offline event(s) from %s", len(queue), config.queue_path)

    cache_path = (
        config.queue_path.parent / "card-status-cache.json"
        if config.queue_path.parent != Path(".")
        else None
    )
    status_cache = CardStatusCache.load(cache_path)

    retry_thread = threading.Thread(
        target=retry_loop,
        args=(config, queue, status_cache),
        name="nfc-retry-loop",
        daemon=True,
    )
    retry_thread.start()

    run(config, queue, status_cache)
    return 0


def run(config: AgentConfig, queue: OfflineQueue, status_cache: CardStatusCache) -> None:
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

            handle_card_scan(config, queue, status_cache, uid)
            last_uid = uid
            last_submit_at = now
            wait_until_card_removed(reader)
        except KeyboardInterrupt:
            raise
        except Exception:
            LOGGER.exception("Unexpected NFC loop error")
            time.sleep(2)


def handle_card_scan(
    config: AgentConfig,
    queue: OfflineQueue,
    status_cache: CardStatusCache,
    card_id: str,
) -> None:
    """Queue first, then submit. The event survives any network failure."""
    scanned_at = utc_now_epoch()
    event_id = uuid.uuid4().hex

    event = QueuedEvent(
        event_id=event_id,
        card_id=card_id,
        terminal_id=config.terminal_id,
        scanned_at_epoch_seconds=scanned_at,
    )
    queue.append(event)

    delivered = try_submit(config, queue, status_cache, event)
    if delivered:
        LOGGER.info("Card %s submitted immediately.", card_id)
    else:
        next_state = next_state_for(status_cache.get(card_id))
        status_cache.update(card_id, next_state)
        LOGGER.warning(
            "API unreachable - card %s queued (%d pending). Local feedback: %s",
            card_id,
            len(queue),
            describe_state(next_state),
        )


def retry_loop(
    config: AgentConfig,
    queue: OfflineQueue,
    status_cache: CardStatusCache,
) -> None:
    """Drains the offline queue in the background with capped backoff.

    Delivery goes to the idempotent sync endpoint (``/api/nfc/clock/sync``)
    so replayed events keep their original scan timestamps and a retry after
    a timeout can never double-toggle. Consecutive successes drain the queue
    without sleeping; the backoff only applies after failures.
    """
    attempt = 0
    while True:
        pending = queue.snapshot()
        if not pending:
            attempt = 0
            time.sleep(2)
            continue

        delay_index = min(attempt, len(RETRY_DELAYS_SECONDS) - 1)
        if attempt > 0:
            time.sleep(RETRY_DELAYS_SECONDS[delay_index])

        event = pending[0]
        delivered = submit_sync(config, queue, status_cache, event)
        if delivered:
            LOGGER.info(
                "Queued event for card %s delivered (scanned %.0f s ago).",
                event.card_id,
                max(0.0, utc_now_epoch() - event.scanned_at_epoch_seconds),
            )
            attempt = 0
        else:
            attempt += 1
            delay_index = min(attempt, len(RETRY_DELAYS_SECONDS) - 1)
            LOGGER.info(
                "Sync still failing; %d event(s) queued. Next retry in %d s.",
                len(queue),
                RETRY_DELAYS_SECONDS[delay_index],
            )


def iso8601_from_epoch(epoch_seconds: float) -> str:
    """Converts an epoch timestamp to the ISO-8601 string the API DTO expects."""
    return datetime.fromtimestamp(epoch_seconds, tz=timezone.utc).isoformat()


def sync_result_state(state: str | None) -> str | None:
    """Maps the server's ClockStatusDto state to the local cache vocabulary."""
    if state == "working":
        return STATE_CLOCKED_IN
    if state == "paused":
        return STATE_PAUSED
    if state == "clockedOut":
        return STATE_CLOCKED_OUT
    return None


def submit_sync(
    config: AgentConfig,
    queue: OfflineQueue,
    status_cache: CardStatusCache,
    event: QueuedEvent,
) -> bool:
    """Delivers one queued event to the idempotent sync endpoint.

    Returns True when the server accepted the event (applied, duplicate or
    permanently rejected) and it can be removed from the queue. Returns False
    when it should be retried later (Kimai down/buffered, network error,
    malformed request). A 4xx is never treated as a silent drop: events are
    only removed when the server explicitly classified them.
    """
    url = f"{config.api_base_url}/api/nfc/clock/sync"
    payload = json.dumps(
        {
            "events": [
                {
                    "eventId": event.event_id,
                    "cardId": event.card_id,
                    "terminalId": event.terminal_id,
                    # The API DTO uses DateTimeOffset - an epoch float would
                    # fail deserialization and lose the stamp entirely.
                    "scannedAt": iso8601_from_epoch(event.scanned_at_epoch_seconds),
                }
            ]
        }
    ).encode("utf-8")

    request = urllib.request.Request(
        url,
        data=payload,
        headers=create_headers(config),
        method="POST",
    )

    LOGGER.debug("Syncing card %s to %s", event.card_id, url)

    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = json.loads(response.read().decode("utf-8"))
            detail = find_event_result(body, event.event_id) or {}
            status = detail.get("status")

            if status == "applied":
                # The server derived the action from Kimai's state; mirror it
                # in the local cache so the next offline toggle is consistent.
                status_cache.update(
                    event.card_id,
                    sync_result_state(detail.get("state"))
                    or next_state_for(status_cache.get(event.card_id)),
                )
                queue.remove(event.event_id)
                LOGGER.info(
                    "Card %s synced: %s", event.card_id, detail.get("message") or "applied"
                )
                return True

            if status == "duplicate":
                # Already processed by an earlier request - nothing to do.
                queue.remove(event.event_id)
                LOGGER.info("Card %s already known to server (duplicate); dropping from queue.",
                            event.card_id)
                return True

            if status == "rejected":
                # Permanent rejection (unknown card etc.) - do not retry forever.
                LOGGER.warning("Card %s permanently rejected: %s",
                               event.card_id, detail.get("message") or status)
                queue.remove(event.event_id)
                return True

            # "buffered" (Kimai down on the server) or an unexpected payload:
            # keep the event queued, the server will apply it later.
            LOGGER.warning(
                "Card %s not applied by server (%s); keeping in queue.",
                event.card_id, status or "no result",
            )
            return False
    except urllib.error.HTTPError as error:
        body_text = error.read().decode("utf-8", errors="replace")
        if error.code == 400:
            # Malformed request (agent bug, e.g. old queue file). Never drop
            # silently - keep the event and surface the error in the logs.
            LOGGER.error("Sync rejected with 400 (payload bug?): %s", body_text[:300])
            return False
        if error.code == 401:
            LOGGER.error(
                "Reader token rejected (401) - check reader_token in the config; "
                "keeping event in queue."
            )
            return False
        LOGGER.warning("Card %s rejected by API (%s): %s",
                       event.card_id, error.code, body_text[:200])
        return False
    except urllib.error.URLError as error:
        LOGGER.warning("Stempeluhr API is not reachable: %s", error.reason)
        return False
    except TimeoutError:
        LOGGER.warning("Stempeluhr API timed out")
        return False


def find_event_result(body: Any, event_id: str) -> dict[str, Any] | None:
    """Finds the per-event result for ``event_id`` in an OfflineSyncResultDto."""
    if not isinstance(body, dict):
        return None
    results = body.get("results")
    if not isinstance(results, list):
        return None
    for entry in results:
        if isinstance(entry, dict) and entry.get("eventId") == event_id:
            return entry
    return None


def try_submit(
    config: AgentConfig,
    queue: OfflineQueue,
    status_cache: CardStatusCache,
    event: QueuedEvent,
) -> bool:
    """Attempts delivery of one queued event. Returns True when the API
    accepted it (or rejected the card as unknown - nothing to retry then)."""
    url = f"{config.api_base_url}/api/nfc/clock"
    payload = json.dumps(
        {
            "eventId": event.event_id,
            "cardId": event.card_id,
            "terminalId": event.terminal_id,
            "scannedAt": event.scanned_at_epoch_seconds,
        }
    ).encode("utf-8")

    request = urllib.request.Request(
        url,
        data=payload,
        headers=create_headers(config),
        method="POST",
    )

    LOGGER.debug("Submitting card %s to %s", event.card_id, url)

    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = json.loads(response.read().decode("utf-8"))
            state = extract_state(body)
            if state is not None:
                status_cache.update(event.card_id, state)
            queue.remove(event.event_id)
            LOGGER.info("Card %s accepted: %s", event.card_id, body.get("message", response.status))
            return True
    except urllib.error.HTTPError as error:
        body_text = error.read().decode("utf-8", errors="replace")
        if error.code == 409:
            # Duplicate (already processed earlier). Treat as delivered.
            LOGGER.info("Card %s already known to server (409); dropping from queue.",
                        event.card_id)
            queue.remove(event.event_id)
            return True
        if error.code == 400:
            # Permanent rejection (unknown card etc.) - do not retry forever.
            LOGGER.warning("Card %s permanently rejected (%s): %s",
                           event.card_id, error.code, body_text[:200])
            queue.remove(event.event_id)
            return True
        LOGGER.warning("Card %s rejected by API (%s): %s",
                       event.card_id, error.code, body_text[:200])
        return False
    except urllib.error.URLError as error:
        LOGGER.warning("Stempeluhr API is not reachable: %s", error.reason)
        return False
    except TimeoutError:
        LOGGER.warning("Stempeluhr API timed out")
        return False


def extract_state(body: dict[str, Any]) -> str | None:
    """Maps the NfcClockEventDto payload to a simple local state string."""
    if not isinstance(body, dict):
        return None
    status = body.get("status") or {}
    state = status.get("state") if isinstance(status, dict) else None
    if state == "paused":
        return STATE_PAUSED
    if status.get("isRunning"):
        return STATE_CLOCKED_IN
    if body.get("success"):
        return STATE_CLOCKED_OUT
    return None


def next_state_for(current: str | None) -> str:
    """Toggles the assumed state while offline."""
    if current == STATE_CLOCKED_IN or current == STATE_PAUSED:
        return STATE_CLOCKED_OUT
    return STATE_CLOCKED_IN


def describe_state(state: str) -> str:
    return {
        STATE_CLOCKED_IN: "angenommen EINGESTEMPELT",
        STATE_PAUSED: "angenommen PAUSE beendet / eingestempelt",
        STATE_CLOCKED_OUT: "angenommen AUSGESTEMPELT",
    }.get(state, state)


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


def create_headers(config: AgentConfig) -> dict[str, str]:
    headers = {
        "Content-Type": "application/json",
        "User-Agent": "StempeluhrNfcAgent/1.1",
    }
    if config.reader_token:
        headers["X-Nfc-Reader-Token"] = config.reader_token

    return headers


if __name__ == "__main__":
    sys.exit(main())
