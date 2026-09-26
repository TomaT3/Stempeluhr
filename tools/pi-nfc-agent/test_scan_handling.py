"""Tests for the publish-and-ack scan handling.

Run manually: python3 test_scan_handling.py
"""

import sys
import threading
import time
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
    handle_card_scan,
    read_version,
)
import stempeluhr_nfc_agent as agent_module  # noqa: E402


def make_config() -> AgentConfig:
    return AgentConfig(
        api_base_url="http://127.0.0.1:1",
        terminal_id="t1",
        debounce_seconds=3,
        reader_name_contains=None,
        local_port=0,
        selection_timeout_seconds=0.2,
    )


def ack_after(server: LocalScanServer, delay: float) -> threading.Thread:
    def worker() -> None:
        threading.Event().wait(delay)
        server.ack_latest()

    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    return thread


def with_server(test) -> None:
    server = LocalScanServer(port=0)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        test(server)
    finally:
        server.shutdown()
        server.server_close()


def test_ack_within_timeout(server: LocalScanServer) -> None:
    acker = ack_after(server, 0.05)
    outcome = handle_card_scan(make_config(), "CARD1", server)
    acker.join(timeout=2)
    assert outcome == "acked", outcome
    scan = server.latest_scan()
    assert scan is not None and scan.consumed, "scan must be published+consumed"


def test_timeout_drops_and_expires(server: LocalScanServer) -> None:
    outcome = handle_card_scan(make_config(), "CARD2", server)
    assert outcome == "dropped", outcome
    scan = server.latest_scan()
    assert scan is not None and scan.consumed, "dropped scan must be expired"
    # A late UI ack must not change anything any more.
    before = server.latest_scan()
    server.ack_latest()
    assert server.latest_scan() == before, "late ack after drop is a no-op"


def test_newer_scan_supersedes(server: LocalScanServer) -> None:
    outcomes: dict[str, str] = {}

    def first() -> None:
        outcomes["A"] = handle_card_scan(make_config(), "CARD_A", server)

    thread = threading.Thread(target=first, daemon=True)
    thread.start()
    time.sleep(0.05)
    outcomes["B"] = handle_card_scan(make_config(), "CARD_B", server)
    thread.join(timeout=5)
    assert outcomes == {"A": "superseded", "B": "dropped"}, outcomes


def test_late_ack_counts_as_acked(server: LocalScanServer) -> None:
    # Force the watchdog verdict to "timeout" even though a real ack arrives
    # during the wait - exactly the race window between timeout and drop.
    real_wait = agent_module._wait_for_ack

    def late_timeout(*args, **kwargs):
        real_wait(*args, **kwargs)
        return "timeout"

    agent_module._wait_for_ack = late_timeout
    try:
        acker = ack_after(server, 0.05)
        outcome = handle_card_scan(make_config(), "CARD5", server)
        acker.join(timeout=2)
    finally:
        agent_module._wait_for_ack = real_wait
    assert outcome == "acked", outcome


def test_config_ignores_legacy_keys() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as tmp_dir:
        path = Path(tmp_dir) / "config.json"
        path.write_text(
            '{"api_base_url": "https://host/", "terminal_id": "pi-1",'
            ' "reader_token": "x", "fallback_mode": "toggle",'
            ' "queue_path": "/var/lib/q.json"}',
            encoding="utf-8",
        )
        config = AgentConfig.load(path)
        assert config.api_base_url == "https://host"
        assert config.terminal_id == "pi-1"
        assert config.local_port == 8737
        assert config.selection_timeout_seconds == 10


def test_read_version() -> None:
    import tempfile

    with tempfile.TemporaryDirectory() as tmp_dir:
        path = Path(tmp_dir) / "VERSION"
        assert read_version(path) == "0.0.0-local", "missing file -> dev version"
        path.write_text("1.2.3\n", encoding="utf-8")
        assert read_version(path) == "1.2.3"


def main() -> int:
    with_server(test_ack_within_timeout)
    with_server(test_timeout_drops_and_expires)
    with_server(test_newer_scan_supersedes)
    with_server(test_late_ack_counts_as_acked)
    test_config_ignores_legacy_keys()
    test_read_version()
    assert (
        AgentConfig.__dataclass_fields__["selection_timeout_seconds"].default == 10
    ), "default selection timeout must be 10s"

    print("ScanHandling: all tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
