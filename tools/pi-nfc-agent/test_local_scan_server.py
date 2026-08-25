"""Self-test for LocalScanServer (run manually: python3 test_local_scan_server.py)"""

import json
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

# The agent module imports smartcard at import time; stub it so this test
# runs on machines without PC/SC installed.
if "smartcard" not in sys.modules:
    try:
        import smartcard.System  # noqa: F401
    except ImportError:
        import types

        sc = types.ModuleType("smartcard")
        exc = types.ModuleType("smartcard.Exceptions")
        sysm = types.ModuleType("smartcard.System")

        class _CardError(Exception):
            pass

        exc.CardConnectionException = _CardError
        exc.NoCardException = _CardError
        sysm.readers = lambda: []
        sc.Exceptions = exc
        sc.System = sysm
        sys.modules["smartcard"] = sc
        sys.modules["smartcard.Exceptions"] = exc
        sys.modules["smartcard.System"] = sysm

from stempeluhr_nfc_agent import (  # noqa: E402
    AgentConfig,
    LocalScanServer,
)


def make_config(queue_path: Path) -> AgentConfig:
    return AgentConfig(
        api_base_url="http://127.0.0.1:1",
        terminal_id="t1",
        reader_token=None,
        debounce_seconds=3,
        reader_name_contains=None,
        queue_path=queue_path,
        local_port=0,
    )


def get_json(url: str):
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            return response.status, json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read().decode("utf-8"))


def post(url: str):
    request = urllib.request.Request(url, data=b"{}", method="POST")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status
    except urllib.error.HTTPError as error:
        error.read()
        return error.code


def main() -> int:
    with tempfile.TemporaryDirectory() as tmp_dir:
        config = make_config(Path(tmp_dir) / "q.json")
        assert (
            AgentConfig.__dataclass_fields__["local_port"].default == 8737
        ), "default port must be 8737"
        assert config.local_port == 0, "explicit port must win"

        # Port 0 -> OS picks a free port; url must reflect the real one.
        server = LocalScanServer(port=0)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            base = server.url
            assert base.startswith("http://127.0.0.1:"), "must bind loopback only"

            # No scan yet -> 404.
            status, body = get_json(f"{base}/scan/latest")
            assert status == 404, f"expected 404 without a scan, got {status}"

            # Publish a scan and read it back.
            server.publish_scan("04AABBCC", 1724265600.0)
            status, body = get_json(f"{base}/scan/latest")
            assert status == 200
            assert body["cardId"] == "04AABBCC"
            assert body["consumed"] is False
            assert body["scannedAt"].startswith("2024-"), body["scannedAt"]

            # A newer scan replaces the previous one.
            time.sleep(0.01)
            server.publish_scan("04DDEEFF", 1724265660.0)
            _, body = get_json(f"{base}/scan/latest")
            assert body["cardId"] == "04DDEEFF", "newer scan must replace older"

            # Ack consumes it.
            status = post(f"{base}/scan/ack")
            assert status == 200, f"ack should return 200, got {status}"
            _, body = get_json(f"{base}/scan/latest")
            assert body["consumed"] is True, "ack must mark scan as consumed"
        finally:
            server.shutdown()
            server.server_close()

        # Unknown paths -> 404.
        server2 = LocalScanServer(port=0)
        threading.Thread(target=server2.serve_forever, daemon=True).start()
        try:
            status, _ = get_json(f"{server2.url}/other/path")
            assert status == 404, f"unknown path should 404, got {status}"

            # CORS: the kiosk UI runs on a DIFFERENT origin than this
            # loopback server - without these headers the browser blocks
            # both the GET response and the POST preflight entirely.
            request = urllib.request.Request(
                f"{server2.url}/scan/latest", method="OPTIONS"
            )
            with urllib.request.urlopen(request, timeout=5) as response:
                assert response.status == 204, (
                    f"OPTIONS preflight should return 204, got {response.status}"
                )
                assert (
                    response.headers.get("Access-Control-Allow-Origin") is not None
                ), "preflight must carry Access-Control-Allow-Origin"
                assert (
                    "POST" in response.headers.get("Access-Control-Allow-Methods", "")
                ), "preflight must allow POST"
                assert (
                    response.headers.get("Access-Control-Allow-Private-Network")
                    == "true"
                ), (
                    "preflight must allow private network access - the kiosk UI "
                    "is served from a public https origin (cloudflare) and "
                    "Chrome requires this header for public->local requests"
                )
            status, headers = get_json(f"{server2.url}/scan/latest")
            assert status in (200, 404), "GET should answer normally"
            try:
                with urllib.request.urlopen(
                    f"{server2.url}/scan/latest", timeout=5
                ) as response:
                    assert (
                        response.headers.get("Access-Control-Allow-Origin") is not None
                    ), "GET responses must carry Access-Control-Allow-Origin"
            except urllib.error.HTTPError as error:
                assert (
                    error.headers.get("Access-Control-Allow-Origin") is not None
                ), "404 GET responses must also carry Access-Control-Allow-Origin"
        finally:
            server2.shutdown()
            server2.server_close()

    print("LocalScanServer: all tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
