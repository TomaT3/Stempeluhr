"""Quick self-test for OfflineQueue (run manually: python3 test_offline_queue.py)"""

import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

from offline_queue import OfflineQueue, QueuedEvent  # noqa: E402


def main() -> int:
    with tempfile.TemporaryDirectory() as tmp_dir:
        queue_path = Path(tmp_dir) / "q.json"

        queue = OfflineQueue.load(queue_path)
        assert len(queue) == 0, "new queue must be empty"

        first = QueuedEvent("abc", "04AABBCC", "t1", 1724265600.0)
        second = QueuedEvent("def", "04DDEEFF", "t1", 1724265660.0)
        queue.append(first)
        queue.append(second)

        # Reload simulates an agent restart (power loss scenario).
        reloaded = OfflineQueue.load(queue_path)
        assert len(reloaded) == 2, f"expected 2 events after reload, got {len(reloaded)}"
        assert reloaded.snapshot()[0].card_id == "04AABBCC", "order must be preserved"

        reloaded.remove("abc")
        after_remove = OfflineQueue.load(queue_path)
        assert len(after_remove) == 1
        assert after_remove.snapshot()[0].event_id == "def"

    print("OfflineQueue: all tests passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
