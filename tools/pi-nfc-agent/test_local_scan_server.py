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
    AGENT_VERSION,
    AgentConfig,
    LocalScanServer,
    origin_of,
    upstream_request,
)

KIOSK = "https://stempeluhr.example.com"
FOREIGN = "https://evil.example.org"


def make_config() -> AgentConfig:
    return AgentConfig(
        api_base_url="http://127.0.0.1:1",
        terminal_id="t1",
        debounce_seconds=3,
        reader_name_contains=None,
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


def request(url: str, method: str = "GET", origin: str | None = None):
    """(status, headers) of a request, optionally sent with an Origin header."""
    data = b"{}" if method == "POST" else None
    req = urllib.request.Request(url, data=data, method=method)
    if origin is not None:
        req.add_header("Origin", origin)
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            response.read()
            return response.status, response.headers
    except urllib.error.HTTPError as error:
        error.read()
        return error.code, error.headers


def test_origin_of() -> None:
    assert origin_of("https://Stempeluhr.example.com/") == KIOSK
    assert origin_of("https://stempeluhr.example.com:443/app") == KIOSK
    assert origin_of("http://nas:5100") == "http://nas:5100"
    assert origin_of("http://nas:80") == "http://nas"
    assert origin_of("http://[::1]:8080") == "http://[::1]:8080"


def test_config_origin() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        path = Path(tmp) / "config.json"
        # A config.json of an existing Pi: no kiosk_origin key.
        path.write_text('{"api_base_url": "https://stempeluhr.example.com/"}', encoding="utf-8")
        assert AgentConfig.load(path).allowed_origin == KIOSK

        path.write_text(
            '{"api_base_url": "http://nas:5100", "kiosk_origin": "https://stempeluhr.example.com"}',
            encoding="utf-8",
        )
        assert AgentConfig.load(path).allowed_origin == KIOSK


def test_origin_restriction() -> None:
    """Issue #13: only the kiosk page may read and ack scans."""
    server = LocalScanServer(port=0, allowed_origin="https://Stempeluhr.example.com:443/")
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        latest = f"{server.url}/scan/latest"
        ack = f"{server.url}/scan/ack"
        server.publish_scan("04AABBCC", 1724265600.0)

        # The kiosk itself: preflight, read and ack work, and the header names it.
        status, headers = request(latest, "OPTIONS", KIOSK)
        assert status == 204, f"kiosk preflight should return 204, got {status}"
        assert headers.get("Access-Control-Allow-Origin") == KIOSK, headers
        status, headers = request(latest, origin=KIOSK)
        assert status == 200, f"kiosk GET should return 200, got {status}"
        assert headers.get("Access-Control-Allow-Origin") == KIOSK, headers

        # A foreign page: refused, without any CORS header, and the scan
        # stays unconsumed for the kiosk.
        for method, url in (("OPTIONS", ack), ("GET", latest), ("POST", ack)):
            status, headers = request(url, method, FOREIGN)
            assert status == 403, f"foreign {method} {url} should return 403, got {status}"
            assert headers.get("Access-Control-Allow-Origin") is None, headers
        assert server.latest_scan().consumed is False, "foreign ack must not consume"

        # No Origin: not a web page (curl, updater) - allowed as before.
        status, _ = request(f"{server.url}/health", origin=FOREIGN)
        assert status == 200, f"health stays open, got {status}"
        status, _ = request(ack, "POST")
        assert status == 200, f"ack without Origin should return 200, got {status}"
        assert server.latest_scan().consumed is True
    finally:
        server.shutdown()
        server.server_close()


def test_terminal_proxy() -> None:
    import http.server
    seen = []
    redirect = False
    class Upstream(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            seen.append((self.path, dict(self.headers)))
            if redirect:
                self.send_response(302)
                self.send_header("Location", "/redirect-target")
                self.end_headers()
                return
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'[]')
        def do_POST(self):
            payload = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            seen.append((self.path, dict(self.headers), payload))
            if redirect:
                self.send_response(307)
                self.send_header("Location", "/redirect-target")
                self.end_headers()
                return
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b'{"results":[]}')
        def log_message(self, *args):
            pass
    upstream = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
    threading.Thread(target=upstream.serve_forever, daemon=True).start()
    config = AgentConfig(api_base_url=f"http://127.0.0.1:{upstream.server_port}",
                         terminal_id="pi-1", terminal_token="private-terminal-token",
                         debounce_seconds=3, reader_name_contains=None)
    agent = LocalScanServer(port=0, allowed_origin=KIOSK, config=config)
    agent.start_background()
    try:
        assert request(agent.url + "/terminal/catalog")[0] == 403
        assert request(agent.url + "/terminal/catalog", origin=FOREIGN)[0] == 403
        assert not seen
        assert request(agent.url + "/terminal/catalog", origin=KIOSK)[0] == 200
        assert seen[-1][0] == "/api/kiosk/catalog"
        assert seen[-1][1]["Authorization"] == "Bearer private-terminal-token"
        assert seen[-1][1]["X-Terminal-Id"] == "pi-1"
        assert seen[-1][1]["User-Agent"].startswith("Stempeluhr-NFC-Agent/"), seen[-1][1]
        payload ={"events": [{"eventId": "e1", "terminalId": "pi-1", "pin": "1234", "nfcCardId": "04AB"}]}
        req = urllib.request.Request(agent.url + "/terminal/sync", data=json.dumps(payload).encode(),
                                     headers={"Origin": KIOSK, "Content-Type": "application/json"})
        with urllib.request.urlopen(req) as response:
            assert response.status == 200
            assert b"private-terminal-token" not in response.read()
        assert seen[-1][0] == "/api/kiosk/clock/sync"
        assert "pin" not in seen[-1][2]["events"][0]
        assert "nfcCardId" not in seen[-1][2]["events"][0]
        payload["events"][0]["terminalId"] = "wrong-terminal"
        req.data = json.dumps(payload).encode()
        with urllib.request.urlopen(req) as response:
            assert response.status == 200
        assert seen[-1][2]["events"][0]["terminalId"] == "pi-1"
        assert len(seen) == 3
        assert request(agent.url + "/terminal/catalog", origin="https://STEMPELUHR.example.com:443")[0] == 200
        assert len(seen) == 4
        for origin in (None, FOREIGN):
            req = urllib.request.Request(agent.url + "/terminal/sync", data=b"x" * 64000)
            if origin:
                req.add_header("Origin", origin)
            try:
                urllib.request.urlopen(req)
                raise AssertionError("foreign/missing origin accepted")
            except urllib.error.HTTPError as error:
                assert error.code == 403
                error.read()
        unconfigured = LocalScanServer(port=0, allowed_origin=KIOSK)
        unconfigured.start_background()
        try:
            req = urllib.request.Request(unconfigured.url + "/terminal/sync", data=b"x" * 64000,
                                         headers={"Origin": KIOSK})
            try:
                urllib.request.urlopen(req)
                raise AssertionError("missing token accepted")
            except urllib.error.HTTPError as error:
                assert error.code == 503
                error.read()
        finally:
            unconfigured.shutdown()
            unconfigured.server_close()
        redirect = True
        assert request(agent.url + "/terminal/catalog", origin=KIOSK)[0] == 502
        assert len(seen) == 5  # Redirect target must not receive the bearer token.
        try:
            with upstream_request(config, "/api/kiosk/diagnostics", b'{"ui":{}}', timeout=5):
                raise AssertionError("diagnostic redirect accepted")
        except urllib.error.HTTPError as error:
            assert error.code == 307
            error.close()
        assert len(seen) == 6
        assert seen[-1][0] == "/api/kiosk/diagnostics"
        assert seen[-1][1]["Authorization"] == "Bearer private-terminal-token"
        assert seen[-1][1]["X-Terminal-Id"] == "pi-1"
        assert seen[-1][1]["User-Agent"] == f"Stempeluhr-NFC-Agent/{AGENT_VERSION}"
    finally:
        agent.shutdown()
        agent.server_close()
        upstream.shutdown()
        upstream.server_close()


def main() -> int:
    test_origin_of()
    test_config_origin()
    test_origin_restriction()

    config = make_config()
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

        # Health reports the agent version (used by update.sh).
        status, body = get_json(f"{base}/health")
        assert status == 200, f"health should return 200, got {status}"
        assert body["ok"] is True
        assert body["version"] == AGENT_VERSION, body

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

    test_terminal_proxy()
    print("LocalScanServer: all tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
