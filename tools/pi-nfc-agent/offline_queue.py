#!/usr/bin/env python3
"""Persistent offline queue for NFC clock events.

Events are appended to a JSON file with fsync so they survive power loss.
The queue is drained by the agent whenever the Stempeluhr API is reachable.
"""

from __future__ import annotations

import json
import os
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class QueuedEvent:
    """A single card scan that must reach the Stempeluhr API exactly once."""

    event_id: str
    card_id: str
    terminal_id: str
    scanned_at_epoch_seconds: float

    def to_dict(self) -> dict[str, Any]:
        return {
            "eventId": self.event_id,
            "cardId": self.card_id,
            "terminalId": self.terminal_id,
            "scannedAt": self.scanned_at_epoch_seconds,
        }

    @staticmethod
    def from_dict(raw: dict[str, Any]) -> "QueuedEvent":
        return QueuedEvent(
            event_id=str(raw["eventId"]),
            card_id=str(raw["cardId"]),
            terminal_id=str(raw["terminalId"]),
            scanned_at_epoch_seconds=float(raw["scannedAt"]),
        )


@dataclass
class OfflineQueue:
    """Append-only JSON file queue with atomic rewrite on drain.

    - append() writes the full file with fsync (survives power loss).
    - remove() rewrites the file atomically (tmp + os.replace).
    - A threading.Lock keeps concurrent retry-loop / scan-loop access safe.
    """

    path: Path
    _events: list[QueuedEvent] = field(default_factory=list)
    _lock: threading.Lock = field(default_factory=threading.Lock)

    @staticmethod
    def load(path: Path) -> "OfflineQueue":
        queue = OfflineQueue(path=path)
        if path.exists():
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                if isinstance(raw, list):
                    queue._events = [
                        item for item in (QueuedEvent.from_dict(e) for e in raw if isinstance(e, dict))
                        if True  # keep ordering as stored
                    ]
            except (json.JSONDecodeError, KeyError, ValueError):
                # Corrupted queue file: move it aside instead of crashing the agent.
                backup = path.with_suffix(".corrupt")
                try:
                    os.replace(path, backup)
                except OSError:
                    pass
        return queue

    def append(self, event: QueuedEvent) -> None:
        with self._lock:
            self._events.append(event)
            self._flush_locked()

    def remove(self, event_id: str) -> None:
        with self._lock:
            before = len(self._events)
            self._events = [e for e in self._events if e.event_id != event_id]
            if len(self._events) != before:
                self._flush_locked()

    def snapshot(self) -> list[QueuedEvent]:
        with self._lock:
            return list(self._events)

    def __len__(self) -> int:
        with self._lock:
            return len(self._events)

    def _flush_locked(self) -> None:
        payload = json.dumps(
            [event.to_dict() for event in self._events],
            ensure_ascii=False,
            indent=1,
        )
        tmp_path = self.path.with_suffix(".tmp")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with open(tmp_path, "w", encoding="utf-8") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp_path, self.path)


def utc_now_epoch() -> float:
    return time.time()
