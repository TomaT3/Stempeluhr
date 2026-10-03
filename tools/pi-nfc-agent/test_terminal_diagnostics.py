"""Diagnostics tests without Pi hardware or a live upstream."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

from terminal_diagnostics import DiagnosticsMonitor, sanitize_heartbeat, SystemSampler
# Reuse the smartcard stub; importing does not start a reader or server.
from test_local_scan_server import LocalScanServer


class DiagnosticsTests(unittest.TestCase):
    def test_javascript_timestamp_is_normalized_for_python39(self):
        import terminal_diagnostics
        from datetime import datetime as real_datetime
        class Python39Datetime:
            @staticmethod
            def fromisoformat(value):
                if value.endswith("Z"):
                    raise ValueError("Python 3.9 does not support Z")
                return real_datetime.fromisoformat(value)
        with patch.object(terminal_diagnostics, "datetime", Python39Datetime):
            clean = sanitize_heartbeat({"events": [{"kind": "error", "code": "promise", "at": "2026-10-03T16:00:00.123Z"}]})
        self.assertEqual(clean["events"][0]["at"], "2026-10-03T16:00:00.123000+00:00")

    def test_accumulates_heartbeats_deduplicates_and_preserves_events_during_forward(self):
        with tempfile.TemporaryDirectory() as folder:
            reports = []
            monitor = DiagnosticsMonitor(Path(folder), "test")
            monitor._sampler.sample = lambda: {}
            def event(seq):
                return {"kind": "http", "operation": "clock", "seq": seq, "at": "2026-10-03T16:00:00Z", "status": 400}
            for i in range(4):
                with patch("terminal_diagnostics.time.monotonic", return_value=100 + i * 15):
                    monitor.accept({"events": [event(max(0, i - 1)), event(i)]})
            def forward(snapshot):
                reports.append(snapshot)
                with patch("terminal_diagnostics.time.monotonic", return_value=170):
                    monitor.accept({"events": [event(3), event(4)]})
            monitor.forward = forward
            monitor.sample()
            self.assertEqual([e["seq"] for e in reports[0]["ui"]["events"]], [0, 1, 2, 3])
            monitor.forward = reports.append
            monitor.sample()
            self.assertEqual([e["seq"] for e in reports[1]["ui"]["events"]], [4])
            monitor.sample()
            self.assertEqual(reports[2]["ui"]["events"], [])
            monitor.close()

    def test_forward_failure_keeps_bounded_events_for_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            monitor = DiagnosticsMonitor(Path(folder), "test", lambda _: (_ for _ in ()).throw(OSError()))
            monitor._sampler.sample = lambda: {}
            for i in range(6):
                with patch("terminal_diagnostics.time.monotonic", return_value=100 + i * 15):
                    monitor.accept({"events": [{"kind": "error", "code": "promise", "seq": i * 20 + n} for n in range(20)]})
            failed = monitor.sample()
            self.assertEqual(len(failed["ui"]["events"]), 80)
            reports = []
            monitor.forward = reports.append
            monitor.sample()
            self.assertEqual(reports[0]["ui"]["events"], failed["ui"]["events"])
            monitor.close()

    def test_state_event_keeps_its_original_state(self):
        clean = sanitize_heartbeat({"screen": "idle", "busy": False,
            "events": [{"kind": "state", "screen": "session", "busy": True,
                        "blocked": "request", "pending": 1, "pin": "SECRET"}]})
        self.assertEqual(clean["events"][0]["screen"], "session")
        self.assertTrue(clean["events"][0]["busy"])
        self.assertNotIn("SECRET", json.dumps(clean))

    def test_allowlist(self):
        raw = {"appVersion": "0.17.1", "screen": "session", "blocked": "backlog", "pending": 2,
               "pin": "SECRET", "name": "SECRET", "requests": [{"operation": "sync", "ageMs": 12, "token": "SECRET"}],
               "events": [{"kind": "http", "operation": "clock", "status": 400, "durationMs": 10,
                           "eventId": "abc-123", "body": "SECRET"}, {"kind": "error", "code": "promise", "message": "SECRET"}]}
        clean = sanitize_heartbeat(raw)
        self.assertNotIn("SECRET", json.dumps(clean))
        self.assertEqual(clean["pending"], 2)
        self.assertIsNone(sanitize_heartbeat({"pending": 10 ** 1000})["pending"])
        self.assertEqual(clean["events"][0]["eventId"], "abc-123")
        for key in ("screen", "blocked", "requests", "events"):
            sanitize_heartbeat({key: {"invalid": "value"}})
        self.assertIsNone(sanitize_heartbeat({"pending": float("nan")})["pending"])

    def test_watchdog_and_persistence(self):
        with tempfile.TemporaryDirectory() as folder:
            monitor = DiagnosticsMonitor(Path(folder), "test")
            with patch("terminal_diagnostics.time.monotonic", return_value=100):
                self.assertTrue(monitor.accept({"busy": True, "screen": "session"}))
                self.assertFalse(monitor.accept({}))
            with patch("terminal_diagnostics.time.monotonic", return_value=175):
                snapshot = monitor.sample()
            self.assertEqual(snapshot["uiStatus"], "missing")
            self.assertEqual(snapshot["heartbeatAgeSeconds"], 75)
            self.assertTrue(snapshot["ui"]["busy"])
            monitor.close()
            self.assertIn('"missing"', (Path(folder) / "diagnostics.jsonl").read_text())
            reopened = DiagnosticsMonitor(Path(folder), "test")
            reopened.sample()
            reopened.close()
            self.assertIn('"missing"', (Path(folder) / "diagnostics.jsonl").read_text())

    def test_rotates_and_isolates_forward_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            def fail(_):
                raise ValueError("SECRET")
            monitor = DiagnosticsMonitor(Path(folder), "test", fail)
            handler = monitor._logger.handlers[0]
            handler.maxBytes = 200
            for _ in range(8):
                monitor.sample()
            monitor.close()
            files = list(Path(folder).glob("diagnostics.jsonl*"))
            self.assertLessEqual(len(files), 3)
            self.assertNotIn("SECRET", "".join(p.read_text() for p in files))

    def test_cpu_and_memory_sample(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "stat").write_text("cpu 10 0 10 80 0 0 0 0\n")
            (root / "meminfo").write_text("MemAvailable: 800000 kB\n")
            (root / "uptime").write_text("100.5 0\n")
            (root / "loadavg").write_text("0.3 0 0\n")
            sampler = SystemSampler(root)
            sampler.sample()
            (root / "stat").write_text("cpu 20 0 20 160 0 0 0 0\n")
            sample = sampler.sample()
            self.assertEqual(sample["cpuPercent"], 20)
            self.assertEqual(sample["availableMemoryKb"], 800000)

    def test_heartbeat_origin_and_size_limits(self):
        with tempfile.TemporaryDirectory() as folder:
            monitor = DiagnosticsMonitor(Path(folder), "test")
            server = LocalScanServer(port=0, allowed_origin="https://kiosk.test", diagnostics=monitor)
            server.start_background()
            def post(origin, data=b"{}"):
                headers = {"Origin": origin} if origin else {}
                request = urllib.request.Request(server.url + "/diagnostics/heartbeat", data=data, headers=headers)
                try:
                    with urllib.request.urlopen(request, timeout=5) as response:
                        return response.status
                except urllib.error.HTTPError as error:
                    with error:
                        error.read()
                        return error.code
            try:
                self.assertEqual(post(None), 403)
                self.assertEqual(post("https://evil.test"), 403)
                self.assertEqual(post("https://kiosk.test", b"x" * 32769), 400)
                self.assertEqual(post("https://kiosk.test", b"[]"), 400)
                self.assertEqual(post("https://kiosk.test"), 200)
                self.assertEqual(post("https://kiosk.test"), 429)
            finally:
                server.shutdown()
                server.server_close()
                monitor.close()


if __name__ == "__main__":
    unittest.main()
