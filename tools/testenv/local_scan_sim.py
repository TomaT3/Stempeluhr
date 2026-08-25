#!/usr/bin/env python3
"""Agent-Level-Simulation des NFC-Agents für den E2E-Test.

Der echte Agent braucht einen PC/SC-Reader (pyscard), der im CI/Container
nicht vorhanden ist. Dieses Skript stubt das ``smartcard``-Modul, importiert
den echten Agent-Code (LocalScanServer + handle_card_scan + OfflineQueue)
und steuert ihn über stdin-Kommandos:

    scan <card_id>   -> publish_scan(card_id) auf dem LocalScanServer
    handle <card_id> -> handle_card_scan(...) inkl. Ack-Watchdog + Fallback
    quit             -> beenden

Damit lässt sich der Offline-Identifikationspfad (Publish → Ack / Timeout →
Fallback) ohne Hardware gegen den laufenden Loopback-Server testen. Die
Grenze: kein echter Browser/Angular-Kiosk - die UI-Seite wird per curl
gegen 127.0.0.1:<port> simuliert (siehe run_e2e_test.sh).
"""

from __future__ import annotations

import sys
import threading
import time
import types
from pathlib import Path


def _stub_smartcard() -> None:
    """Install minimal fake ``smartcard`` modules so the agent imports.

    Only used when pyscard is not installed on this machine.
    """
    try:
        import smartcard  # noqa: F401
        return
    except ImportError:
        pass
    sc = types.ModuleType("smartcard")
    sc.__path__ = []  # mark as package so "smartcard.Exceptions" imports
    exceptions = types.ModuleType("smartcard.Exceptions")


    class CardConnectionException(Exception):
        pass

    class NoCardException(Exception):
        pass

    exceptions.CardConnectionException = CardConnectionException
    exceptions.NoCardException = NoCardException

    system = types.ModuleType("smartcard.System")
    system.readers = lambda: []  # never called in this simulation

    sys.modules["smartcard"] = sc
    sys.modules["smartcard.Exceptions"] = exceptions
    sys.modules["smartcard.System"] = system


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: local_scan_sim.py <config.json> <queue.json>", file=sys.stderr)
        return 2

    config_path = Path(sys.argv[1])
    queue_path = Path(sys.argv[2])

    # Import the real agent code from tools/pi-nfc-agent/.
    agent_dir = Path(__file__).resolve().parent.parent / "pi-nfc-agent"
    sys.path.insert(0, str(agent_dir))
    _stub_smartcard()
    import stempeluhr_nfc_agent as agent  # noqa: E402

    config = agent.AgentConfig.load(config_path)
    queue = agent.OfflineQueue.load(queue_path)
    status_cache = agent.CardStatusCache.load(None)
    scan_server = agent.LocalScanServer(port=config.local_port)
    scan_server.start_background()

    # Report the actually bound port so the test driver can curl it.
    print(f"SIM_READY {scan_server.url}", flush=True)

    workers: list[threading.Thread] = []
    # readline() instead of `for line in sys.stdin`: the iterator uses
    # read-ahead buffering on pipes/FIFOs, so commands would only be seen
    # when the writer closes the pipe - the watchdog tests need them live.
    while True:
        line = sys.stdin.readline()
        if not line:
            break
        parts = line.split()
        if not parts:
            continue
        cmd = parts[0]
        if cmd == "scan" and len(parts) == 2:
            scan_server.publish_scan(parts[1], agent.utc_now_epoch())
            print(f"SIM_PUBLISHED {parts[1]}", flush=True)
        elif cmd == "handle" and len(parts) == 2:
            card_id = parts[1]

            def _run(card_id: str = card_id) -> None:
                try:
                    agent.handle_card_scan(
                        config, queue, status_cache, card_id, scan_server
                    )
                finally:
                    print(f"SIM_HANDLED {card_id} queue_len={len(queue)}", flush=True)

            t = threading.Thread(target=_run, daemon=True)
            t.start()
            workers.append(t)
        elif cmd == "drain":
            for t in workers:
                t.join(timeout=15)
            print("SIM_DRAINED", flush=True)
        elif cmd == "quit":
            break

    scan_server.shutdown()
    scan_server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
