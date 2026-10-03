"""Bounded, credential-free diagnostics independent of Chromium's event loop."""
from __future__ import annotations

import json
from collections import deque
import logging
from logging.handlers import RotatingFileHandler
import math
import os
from pathlib import Path
import re
import shutil
import subprocess
import threading
import time
from datetime import datetime, timezone
from typing import Callable

OPERATIONS = {"clock", "sync", "login", "identify", "hours", "health"}


def number(value, maximum=1_000_000_000):
    return value if type(value) in (int, float) and 0 <= value <= maximum and math.isfinite(value) else None


def sanitize_heartbeat(raw: dict) -> dict:
    """Allowlist fields at the trust boundary; ignore unknown keys and text."""
    result = {}
    if isinstance(raw.get("appVersion"), str) and re.fullmatch(r"[0-9A-Za-z.+-]{1,64}", raw["appVersion"]):
        result["appVersion"] = raw["appVersion"]
    for key, choices in (("screen", {"idle", "session"}), ("blocked", {"none", "request", "backlog", "status"})):
        if isinstance(raw.get(key), str) and raw[key] in choices:
            result[key] = raw[key]
    for key in ("busy", "offline", "visible"):
        if type(raw.get(key)) is bool:
            result[key] = raw[key]
    for key in ("pending", "rejected", "lagMs", "lastInputAgeMs"):
        result[key] = number(raw.get(key))
    result["requests"] = []
    requests = raw.get("requests")
    if isinstance(requests, list):
        for entry in requests[:10]:
            if isinstance(entry, dict) and isinstance(entry.get("operation"), str) and entry["operation"] in OPERATIONS:
                result["requests"].append({"operation": entry["operation"], "ageMs": number(entry.get("ageMs"))})
    result["events"] = []
    events = raw.get("events")
    if isinstance(events, list):
        for entry in events[-20:]:
            if not isinstance(entry, dict):
                continue
            if entry.get("kind") == "http" and isinstance(entry.get("operation"), str) and entry["operation"] in OPERATIONS:
                event = {"kind": "http", "operation": entry["operation"], "status": entry.get("status")
                         if type(entry.get("status")) is int and -1 <= entry["status"] <= 599 else None,
                         "durationMs": number(entry.get("durationMs"))}
                event_id = entry.get("eventId")
                if isinstance(event_id, str) and re.fullmatch(r"[0-9A-Za-z-]{1,64}", event_id):
                    event["eventId"] = event_id
            elif entry.get("kind") == "error" and entry.get("code") in ("javascript", "promise", "storage"):
                event = {"kind": "error", "code": entry["code"]}
            elif entry.get("kind") == "state":
                event = {"kind": "state"}
                for key, choices in (("screen", {"idle", "session"}), ("blocked", {"none", "request", "backlog", "status"})):
                    if isinstance(entry.get(key), str) and entry[key] in choices:
                        event[key] = entry[key]
                for key in ("busy", "offline"):
                    if type(entry.get(key)) is bool:
                        event[key] = entry[key]
                for key in ("pending", "rejected"):
                    event[key] = number(entry.get(key))
            else:
                continue
            try:
                parsed = datetime.fromisoformat(str(entry.get("at")).replace("Z", "+00:00"))
                if parsed.tzinfo is not None:
                    event["at"] = parsed.astimezone(timezone.utc).isoformat()
            except (ValueError, OverflowError):
                pass
            if type(entry.get("seq")) is int and 0 <= entry["seq"] <= 9_007_199_254_740_991:
                event["seq"] = entry["seq"]
            result["events"].append(event)
    return result


class SystemSampler:
    def __init__(self, proc: Path = Path("/proc")):
        self.proc = proc
        self.previous_cpu = None

    def sample(self) -> dict:
        values = {}
        try:
            ticks = [int(n) for n in (self.proc / "stat").read_text().splitlines()[0].split()[1:9]]
            total, idle = sum(ticks), ticks[3] + ticks[4]
            if self.previous_cpu:
                old_total, old_idle = self.previous_cpu
                if total > old_total:
                    values["cpuPercent"] = round(100 * (1 - (idle - old_idle) / (total - old_total)), 1)
            self.previous_cpu = (total, idle)
            memory = dict(line.split(":", 1) for line in (self.proc / "meminfo").read_text().splitlines())
            values["availableMemoryKb"] = int(memory["MemAvailable"].split()[0])
            values["uptimeSeconds"] = int(float((self.proc / "uptime").read_text().split()[0]))
            values["load1"] = float((self.proc / "loadavg").read_text().split()[0])
        except (OSError, ValueError, KeyError, IndexError):
            pass
        chromium_rss, chromium_count, pcscd_rss = 0, 0, 0
        try:
            for path in self.proc.iterdir():
                if not path.name.isdecimal():
                    continue
                try:
                    name = (path / "comm").read_text().strip()
                    if name not in ("chromium", "pcscd"):
                        continue
                    status = dict(line.split(":", 1) for line in (path / "status").read_text().splitlines() if ":" in line)
                    rss = int(status["VmRSS"].split()[0])
                    if name == "chromium":
                        chromium_count += 1
                        chromium_rss += rss
                    else:
                        pcscd_rss += rss
                except (OSError, ValueError, KeyError):
                    continue
            values.update(chromiumProcesses=chromium_count, chromiumRssSumKb=chromium_rss, pcscdRssKb=pcscd_rss)
        except OSError:
            pass
        try:
            values["temperatureC"] = round(float(Path("/sys/class/thermal/thermal_zone0/temp").read_text()) / 1000, 1)
        except (OSError, ValueError):
            pass
        try:
            result = subprocess.run(["vcgencmd", "get_throttled"], capture_output=True, text=True, timeout=2, check=True)
            values["throttledFlags"] = int(result.stdout.strip().split("=")[1], 16)
        except (OSError, ValueError, IndexError, subprocess.SubprocessError):
            pass
        try:
            values["diskFreeMb"] = shutil.disk_usage("/").free // (1024 * 1024)
        except OSError:
            pass
        return values


class DiagnosticsMonitor:
    def __init__(self, directory: Path, version: str, forward: Callable[[dict], None] | None = None):
        self.version = version
        self.forward = forward
        self._lock = threading.Lock()
        self._last_seen = None
        self._ui = None
        self._snapshot = {}
        self._last_accepted = None
        self._pending_events = []
        self._seen_events = deque(maxlen=160)
        self._event_sequence = 0
        self._stop = threading.Event()
        self._sampler = SystemSampler()
        self._logger = logging.Logger("terminal-diagnostics")
        try:
            directory.mkdir(parents=True, exist_ok=True)
            handler = RotatingFileHandler(directory / "diagnostics.jsonl", maxBytes=2 * 1024 * 1024, backupCount=2, encoding="utf-8")
            handler.setFormatter(logging.Formatter("%(message)s"))
            self._logger.addHandler(handler)
        except OSError:
            logging.getLogger("stempeluhr-nfc-agent").warning("Diagnostic history unavailable; stamping remains enabled")
            self._logger.addHandler(logging.NullHandler())

    def accept(self, raw: dict) -> bool:
        now = time.monotonic()
        with self._lock:
            if self._last_accepted is not None and now - self._last_accepted < 5:
                return False
            self._last_accepted = now
            self._ui = sanitize_heartbeat(raw)
            for event in self._ui["events"]:
                # seq restarts when Chromium reloads, so include the timestamp.
                # Legacy heartbeats without seq are deduplicated by content.
                key = json.dumps(event, sort_keys=True)
                if key not in self._seen_events:
                    self._seen_events.append(key)
                    self._event_sequence += 1
                    self._pending_events.append((self._event_sequence, event))
            self._pending_events = self._pending_events[-80:]
            self._last_seen = now
            ui = self._ui.copy()
        self._write({"kind": "heartbeat", "ui": ui})
        return True

    def sample(self) -> dict:
        with self._lock:
            age = None if self._last_seen is None else round(time.monotonic() - self._last_seen, 1)
            sent_events = self._pending_events.copy()
            ui = None if self._ui is None else {**self._ui, "events": [event for _, event in sent_events]}
        snapshot = {"agentVersion": self.version, "heartbeatAgeSeconds": age,
                    "uiStatus": "not-seen" if age is None else "missing" if age > 60 else "alive",
                    "ui": ui, "system": self._sampler.sample()}
        with self._lock:
            self._snapshot = snapshot
        self._write({"kind": "sample", **snapshot})
        if self.forward:
            try:
                self.forward(snapshot)
                sent_ids = {seq for seq, _ in sent_events}
                with self._lock:
                    self._pending_events = [(seq, event) for seq, event in self._pending_events if seq not in sent_ids]
            except Exception:
                # No credentials, upstream body or URLs in diagnostic errors.
                self._write({"kind": "forward-failed"})
        return snapshot

    def snapshot(self) -> dict:
        with self._lock:
            return self._snapshot.copy()

    def _write(self, record: dict):
        self._logger.info(json.dumps({"at": datetime.now(timezone.utc).isoformat(), **record}, ensure_ascii=True))

    def start(self):
        def run():
            while not self._stop.is_set():
                try:
                    self.sample()
                except Exception:
                    logging.getLogger("stempeluhr-nfc-agent").warning("Diagnostic sample failed")
                self._stop.wait(60)
        self._thread = threading.Thread(target=run, name="terminal-diagnostics", daemon=True)
        self._thread.start()

    def close(self):
        self._stop.set()
        if hasattr(self, "_thread"):
            self._thread.join(timeout=5)
        for handler in self._logger.handlers:
            handler.close()


def state_directory() -> Path:
    return Path(os.environ.get("STATE_DIRECTORY", "/var/lib/stempeluhr-nfc-agent").split(":")[0])
