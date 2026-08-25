#!/usr/bin/env python3
"""Minimaler Fake-Kimai-Server für Stempeluhr-End-to-End-Tests.

Simuliert die Kimai-REST-Endpoints, die der StempeluhrKimaiClient nutzt:
- GET  /api/timesheets/active        -> aktives Timesheet (Array)
- POST /api/timesheets?full=true     -> Timesheet erstellen (begin akzeptiert)
- PATCH /api/timesheets/{id}         -> begin/end nachtragen
- PATCH /api/timesheets/{id}/stop    -> Timesheet stoppen (end = jetzt)

Alle Buchungen werden im Log (JSONL) protokolliert und als In-Memory-Liste
gehalten. Ein GET /_bookings gibt den kompletten Zustand zurück - damit
verifiziert der Test die Nachträge.
"""

from __future__ import annotations

import json
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOCK = threading.Lock()
TIMESHEETS: list[dict] = []
NEXT_ID = 100
LOG_PATH = "fake_kimai_log.jsonl"


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def log(action: str, payload: dict) -> None:
    entry = {"ts": now_iso(), "action": action, **payload}
    with open(LOG_PATH, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(entry) + "\n")
    print("LOG:", json.dumps(entry), flush=True)


class Handler(BaseHTTPRequestHandler):
    def _send(self, status: int, body) -> None:
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _read_json(self) -> dict:
        length = int(self.headers.get("Content-Length") or 0)
        if not length:
            return {}
        try:
            return json.loads(self.rfile.read(length))
        except json.JSONDecodeError:
            return {}

    def log_message(self, format, *args):  # noqa: A002 - Signatur der Basisklasse
        pass

    def do_GET(self):
        if self.path.startswith("/api/timesheets/active"):
            with LOCK:
                active = [t for t in TIMESHEETS if t.get("end") is None]
                # Kimai liefert nur das erste aktive Timesheet
                self._send(200, active[:1])
            return
        if self.path.startswith("/_bookings"):
            with LOCK:
                self._send(200, {"timesheets": TIMESHEETS})
            return
        self._send(404, {"error": "not found"})

    def do_POST(self):
        global NEXT_ID
        if self.path.startswith("/api/timesheets"):
            body = self._read_json()
            with LOCK:
                sheet = {
                    "id": NEXT_ID,
                    "begin": body.get("begin") or now_iso(),
                    "end": None,
                    "project": body.get("project"),
                    "activity": body.get("activity"),
                    "description": body.get("description"),
                    "billable": body.get("billable", True),
                }
                NEXT_ID += 1
                TIMESHEETS.append(sheet)
            log("create", sheet)
            self._send(200, sheet)
            return
        self._send(404, {"error": "not found"})

    def do_PATCH(self):
        parts = self.path.strip("/").split("/")
        # api/timesheets/{id}[/stop]
        if len(parts) >= 3 and parts[2].isdigit():
            sheet_id = int(parts[2])
            stop = len(parts) >= 4 and parts[3] == "stop"
            body = self._read_json()
            with LOCK:
                sheet = next((t for t in TIMESHEETS if t["id"] == sheet_id), None)
                if sheet is None:
                    self._send(404, {"error": "timesheet not found"})
                    return
                if stop:
                    # Kimai setzt beim Stoppen die Endzeit auf jetzt;
                    # ein anschließendes PATCH {end} übersteuert sie.
                    sheet["end"] = now_iso()
                if "begin" in body:
                    sheet["begin"] = body["begin"]
                if "end" in body:
                    sheet["end"] = body["end"]
                log("patch-stop" if stop else "patch", sheet)
                self._send(200, sheet)
            return
        self._send(404, {"error": "not found"})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", 8099), Handler)
    print("Fake Kimai lauscht auf http://127.0.0.1:8099")
    server.serve_forever()
