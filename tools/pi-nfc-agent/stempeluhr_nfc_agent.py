#!/usr/bin/env python3
"""Read an ACR122U NFC reader via PC/SC and submit cards to Stempeluhr."""

from __future__ import annotations

import argparse
import json
import logging
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from smartcard.Exceptions import CardConnectionException, NoCardException
from smartcard.System import readers


LOGGER = logging.getLogger("stempeluhr-nfc-agent")
GET_UID_APDU = [0xFF, 0xCA, 0x00, 0x00, 0x00]


@dataclass(frozen=True)
class AgentConfig:
    api_base_url: str
    terminal_id: str
    reader_token: str | None
    debounce_seconds: float
    reader_name_contains: str | None

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
        )


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
    run(config)
    return 0


def run(config: AgentConfig) -> None:
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

            LOGGER.info("Read NFC card UID %s", uid)

            now = time.monotonic()
            if uid == last_uid and now - last_submit_at < config.debounce_seconds:
                time.sleep(0.2)
                continue

            submit_card(config, uid)
            last_uid = uid
            last_submit_at = now
            wait_until_card_removed(reader)
        except KeyboardInterrupt:
            raise
        except Exception:
            LOGGER.exception("Unexpected NFC loop error")
            time.sleep(2)


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


def submit_card(config: AgentConfig, uid: str) -> None:
    url = f"{config.api_base_url}/api/nfc/clock"
    payload = json.dumps(
        {
            "cardId": uid,
            "terminalId": config.terminal_id,
        }
    ).encode("utf-8")

    request = urllib.request.Request(
        url,
        data=payload,
        headers=create_headers(config),
        method="POST",
    )

    LOGGER.info("Submitting card %s to %s", uid, url)

    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = json.loads(response.read().decode("utf-8"))
            LOGGER.info("Card %s submitted: %s", uid, body.get("message", response.status))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        LOGGER.warning("Card %s rejected by API (%s): %s", uid, error.code, body)
    except urllib.error.URLError as error:
        LOGGER.warning("Stempeluhr API is not reachable: %s", error.reason)


def create_headers(config: AgentConfig) -> dict[str, str]:
    headers = {
        "Content-Type": "application/json",
        "User-Agent": "StempeluhrNfcAgent/1.0",
    }
    if config.reader_token:
        headers["X-Nfc-Reader-Token"] = config.reader_token

    return headers


if __name__ == "__main__":
    sys.exit(main())
