"""Tests for the publish-instead-of-toggle scan handling.

Run manually: python3 test_scan_handling.py
"""

import json
import sys
import tempfile
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
    CardStatusCache,
    LocalScanServer,
    handle_card_scan,
)
from offline_queue import OfflineQueue  # noqa: E402
import stempeluhr_nfc_agent as agent_module  # noqa: E402


def make_config(queue_path: Path, fallback_mode: str = "none") -> AgentConfig:
    return AgentConfig(
        api_base_url="http://127.0.0.1:1",
        terminal_id="t1",
        reader_token=None,
        debounce_seconds=3,
        reader_name_contains=None,
        queue_path=queue_path,
        local_port=0,
        selection_timeout_seconds=0.2,
        fallback_mode=fallback_mode,
    )


def ack_after(server: LocalScanServer, delay: float) -> threading.Thread:
    def worker() -> None:
        threading.Event().wait(delay)
        server.ack_latest()

    thread = threading.Thread(target=worker, daemon=True)
    thread.start()
    return thread


def main() -> int:
    with tempfile.TemporaryDirectory() as tmp_dir:
        tmp = Path(tmp_dir)

        # 1) Ack within the timeout -> published, NOT queued.
        config = make_config(tmp / "q1.json")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            acker = ack_after(server, 0.05)
            handle_card_scan(config, queue, cache, "CARD1", server)
            acker.join(timeout=2)
            assert len(queue) == 0, f"acked scan must not be queued, got {len(queue)}"
            scan = server.latest_scan()
            assert scan is not None and scan.consumed, "scan must be published+consumed"
        finally:
            server.shutdown()
            server.server_close()

        # 2) Timeout + fallback_mode "none" -> no queue event.
        config = make_config(tmp / "q2.json", fallback_mode="none")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            handle_card_scan(config, queue, cache, "CARD2", server)
            assert len(queue) == 0, "mode 'none' must not queue on timeout"
        finally:
            server.shutdown()
            server.server_close()

        # 3) Timeout + fallback_mode "toggle" -> queued like before.
        config = make_config(tmp / "q3.json", fallback_mode="toggle")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            handle_card_scan(config, queue, cache, "CARD3", server)
            assert len(queue) == 1, "mode 'toggle' must queue on timeout"
            pending = queue.snapshot()
            assert pending[0].card_id == "CARD3"
            # Toggle chain preserved: second timeout flips the cached state.
            handle_card_scan(config, queue, cache, "CARD3", server)
            assert len(queue) == 2, "second scan must queue again"
        finally:
            server.shutdown()
            server.server_close()

        # 4) A newer scan replaces the previous one before the timeout;
        #    only the newest card reaches the fallback.
        config = make_config(tmp / "q4.json", fallback_mode="toggle")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            first = threading.Thread(
                target=handle_card_scan,
                args=(config, queue, cache, "CARD_A", server),
                daemon=True,
            )
            first.start()
            time.sleep(0.05)
            handle_card_scan(config, queue, cache, "CARD_B", server)
            first.join(timeout=5)
            pending = queue.snapshot()
            cards = {event.card_id for event in pending}
            assert cards == {"CARD_B"}, f"only newest scan may fall back, got {cards}"
            assert len(pending) == 1
        finally:
            server.shutdown()
            server.server_close()

        # 5) Late-ack race: the ack lands in the window between the
        #    watchdog reporting "timeout" and the fallback executing ->
        #    no queue event, treated like "acked".
        config = make_config(tmp / "q5.json", fallback_mode="toggle")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            # Force the watchdog verdict to "timeout" even though a real
            # ack arrives during the wait - this is exactly the late-ack
            # race window between timeout expiry and the fallback.
            real_wait = agent_module._wait_for_ack

            def late_timeout(*args, **kwargs):
                real_wait(*args, **kwargs)
                return "timeout"

            agent_module._wait_for_ack = late_timeout
            try:
                acker = ack_after(server, 0.05)
                handle_card_scan(config, queue, cache, "CARD5", server)
                acker.join(timeout=2)
            finally:
                agent_module._wait_for_ack = real_wait
            assert len(queue) == 0, f"late-acked scan must not be queued, got {len(queue)}"
            scan = server.latest_scan()
            assert scan is not None and scan.consumed, "scan must be consumed"
        finally:
            server.shutdown()
            server.server_close()

        # 6) After the fallback fired, the scan is EXPIRED: a late UI ack
        #    must not consume an already-handled scan (which would let the
        #    client queue AND the agent toggle both act on the same tap).
        config = make_config(tmp / "q6.json", fallback_mode="none")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            handle_card_scan(config, queue, cache, "CARD6", server)
            scan = server.latest_scan()
            assert scan is not None, "scan must still be present"
            assert (
                scan.consumed
            ), "fallback (mode none) must expire the scan immediately"
            # A late UI ack must not resurrect the scan: the fallback already
            # owns it, so acking again must not change any state the watchdog
            # or a second fallback could act on.
            before = server.latest_scan()
            server.ack_latest()
            assert server.latest_scan() == before, "late ack after fallback is a no-op"
        finally:
            server.shutdown()
            server.server_close()

        # 6b) Toggle variant: the fallback queued + expired the scan; a late
        #     ack must not enable a second toggle for the same tap (the
        #     client queue and the agent toggle would both act otherwise).
        config = make_config(tmp / "q6b.json", fallback_mode="toggle")
        queue = OfflineQueue.load(config.queue_path)
        cache = CardStatusCache.load(None)
        server = LocalScanServer(port=0)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            handle_card_scan(config, queue, cache, "CARD7", server)
            assert len(queue) == 1, "toggle fallback must queue once"
            scan = server.latest_scan()
            assert scan is not None and scan.consumed, (
                "toggle fallback must expire the scan immediately"
            )
            # A subsequent late ack cannot re-arm anything: state unchanged.
            before = server.latest_scan()
            server.ack_latest()
            assert server.latest_scan() == before
            assert len(queue) == 1, "late ack must not produce a second event"
        finally:
            server.shutdown()
            server.server_close()

        # Config defaults.
        assert (
            AgentConfig.__dataclass_fields__["selection_timeout_seconds"].default == 10
        ), "default selection timeout must be 10s"
        assert (
            AgentConfig.__dataclass_fields__["fallback_mode"].default == "none"
        ), "default fallback mode must be 'none'"

    print("ScanHandling: all tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
