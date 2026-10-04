"""PC/SC lifecycle regressions, without a card reader or daemon."""
import time
import unittest
from unittest.mock import Mock, patch

# Also works in CI without pyscard installed.
import test_local_scan_server  # noqa: F401
import stempeluhr_nfc_agent as agent

READER = "ACS ACR122U PICC Interface 00 00"


class FakePcsc:
    """Minimal pcscd: one reader, scripted card events per status wait."""

    SCARD_S_SUCCESS = 0
    SCARD_SCOPE_USER = 0
    SCARD_SHARE_SHARED = 2
    SCARD_PROTOCOL_T0 = 1
    SCARD_PROTOCOL_T1 = 2
    SCARD_UNPOWER_CARD = 2
    SCARD_STATE_UNAWARE = 0x0
    SCARD_STATE_CHANGED = 0x2
    SCARD_STATE_UNKNOWN = 0x4
    SCARD_STATE_UNAVAILABLE = 0x8
    SCARD_STATE_EMPTY = 0x10
    SCARD_STATE_PRESENT = 0x20
    SCARD_E_TIMEOUT = 0x8010000A
    SCARD_E_NO_SMARTCARD = 0x8010000C
    SCARD_E_SHARING_VIOLATION = 0x8010000B
    SCARD_E_PROTO_MISMATCH = 0x8010000F
    SCARD_E_NOT_TRANSACTED = 0x80100016
    SCARD_E_NO_SERVICE = 0x8010001D
    SCARD_E_NO_READERS_AVAILABLE = 0x8010002E
    SCARD_W_UNRESPONSIVE_CARD = 0x80100066
    SCARD_W_UNPOWERED_CARD = 0x80100067
    SCARD_W_RESET_CARD = 0x80100068
    SCARD_W_REMOVED_CARD = 0x80100069

    def __init__(self, *steps):
        self.readers = [READER]
        self.steps = list(steps)
        self.present = False
        self.events = 0
        self.unknown = False
        self.service_error = None
        self.contexts = self.released = self.listed = self.connects = self.disconnects = 0
        self.connect_result = None
        self.transmit_result = None

    # Script steps: called before each status wait; True consumes the step.
    def insert(self):
        self.present, self.events = True, self.events + 1
        return True

    def remove(self):
        self.present, self.events = False, self.events + 1
        return True

    def SCardEstablishContext(self, scope):
        self.contexts += 1
        return self.SCARD_S_SUCCESS, self.contexts

    def SCardListReaders(self, context, groups):
        self.listed += 1
        if not self.readers:
            return self.SCARD_E_NO_READERS_AVAILABLE, []
        return self.SCARD_S_SUCCESS, list(self.readers)

    def SCardReleaseContext(self, context):
        self.released += 1
        return self.SCARD_S_SUCCESS

    def SCardGetStatusChange(self, context, timeout, states):
        if not self.steps:
            raise KeyboardInterrupt  # ends agent.run() in tests
        step = self.steps[0]
        if step(self):
            self.steps.pop(0)
        if self.service_error is not None:
            return self.service_error, []
        reader, current = states[0]
        state = self.SCARD_STATE_UNKNOWN if self.unknown else \
            self.SCARD_STATE_PRESENT if self.present else self.SCARD_STATE_EMPTY
        event = (self.events << 16) | state
        if event == current:
            return self.SCARD_E_TIMEOUT, states
        return self.SCARD_S_SUCCESS, [(reader, event | self.SCARD_STATE_CHANGED, [])]

    def SCardConnect(self, context, reader, mode, protocols):
        self.connects += 1
        if self.connect_result is not None:
            return self.connect_result, 0, 0
        if not self.present:
            return self.SCARD_E_NO_SMARTCARD, 0, 0
        return self.SCARD_S_SUCCESS, 7, self.SCARD_PROTOCOL_T1

    def SCardTransmit(self, card, protocol, apdu):
        return self.transmit_result or (self.SCARD_S_SUCCESS, [0x04, 0xA1, 0x90, 0x00])

    def SCardDisconnect(self, card, disposition):
        self.disconnects += 1
        return self.SCARD_S_SUCCESS

    def SCardGetErrorMessage(self, hresult):
        return f"error {hresult:#x}"


def idle(fake):
    return True


def hold(seconds):
    # Card stays on the reader for ``seconds`` from the first wait on.
    started = []
    def step(fake):
        started.append(time.monotonic())
        return started[-1] - started[0] >= seconds
    return step


def fail_with(hresult):
    def step(fake):
        fake.service_error = hresult
        return True
    return step


def recover(fake):
    fake.service_error = None
    return True


class ReaderTests(unittest.TestCase):
    def setUp(self):
        self.config = agent.AgentConfig("https://kiosk.test", "test", 3, "ACR122")

    def run_agent(self, fake, config=None):
        with patch.object(agent, "scard", fake), \
             patch.object(agent.time, "sleep"), \
             patch.object(agent, "handle_card_scan", return_value="acked") as publish:
            with self.assertRaises(KeyboardInterrupt):
                agent.run(config or self.config, Mock())
        return publish

    def test_idle_waits_without_new_contexts_or_card_connections(self):
        fake = FakePcsc(*([idle] * 1000))
        session = agent.ReaderSession("ACR122")
        with patch.object(agent, "scard", fake):
            for _ in range(1000):
                self.assertTrue(session.open())
                self.assertFalse(session.wait_for_card())
            session.close()
            session.close()
        self.assertEqual((fake.contexts, fake.listed, fake.connects, fake.released), (1, 1, 0, 1))

    def test_each_tap_connects_to_the_card_exactly_once(self):
        fake = FakePcsc(FakePcsc.insert, hold(0.05), FakePcsc.remove, idle, FakePcsc.insert, FakePcsc.remove)
        publish = self.run_agent(fake, agent.AgentConfig("https://kiosk.test", "test", 0, None))
        self.assertEqual([call.args[1] for call in publish.call_args_list], ["04A1", "04A1"])
        self.assertEqual((fake.connects, fake.disconnects, fake.contexts, fake.released), (2, 2, 1, 1))

    def test_same_card_within_debounce_is_ignored_until_removed(self):
        fake = FakePcsc(FakePcsc.insert, FakePcsc.remove, FakePcsc.insert, FakePcsc.remove)
        publish = self.run_agent(fake)
        self.assertEqual(publish.call_count, 1)

    def test_same_card_still_held_after_debounce_is_a_new_scan(self):
        fake = FakePcsc(FakePcsc.insert, FakePcsc.remove, FakePcsc.insert, hold(0.1), FakePcsc.remove)
        publish = self.run_agent(fake, agent.AgentConfig("https://kiosk.test", "test", 0.05, None))
        self.assertEqual(publish.call_count, 2)

    def test_card_swapped_between_waits_counts_as_removal(self):
        fake = FakePcsc(FakePcsc.insert)
        session = agent.ReaderSession(None)
        with patch.object(agent, "scard", fake):
            session.open()
            self.assertTrue(session.wait_for_card())
            fake.steps = [lambda f: f.remove() and f.insert()]
            self.assertTrue(session.wait_for_removal())
            self.assertTrue(session.wait_for_card())

    def test_card_level_errors_keep_the_context(self):
        for field, value in (("connect_result", FakePcsc.SCARD_W_UNRESPONSIVE_CARD),
                             ("transmit_result", (FakePcsc.SCARD_W_REMOVED_CARD, [])),
                             ("transmit_result", (FakePcsc.SCARD_S_SUCCESS, [0x63, 0x00]))):
            with self.subTest(field=field, value=value):
                fake = FakePcsc(FakePcsc.insert)
                setattr(fake, field, value)
                session = agent.ReaderSession(None)
                with patch.object(agent, "scard", fake):
                    session.open()
                    session.wait_for_card()
                    self.assertIsNone(session.read_uid())
                self.assertEqual(fake.disconnects, 0 if field == "connect_result" else 1)
                self.assertEqual(fake.released, 0)

    def test_unreadable_held_card_is_retried_without_publishing(self):
        fake = FakePcsc(FakePcsc.insert)
        fake.transmit_result = (FakePcsc.SCARD_W_UNRESPONSIVE_CARD, [])
        with patch.object(agent, "scard", fake), \
             patch.object(agent.time, "sleep", side_effect=[None, None, KeyboardInterrupt()]), \
             patch.object(agent, "handle_card_scan") as publish:
            with self.assertRaises(KeyboardInterrupt):
                agent.run(self.config, Mock())
        publish.assert_not_called()
        self.assertEqual((fake.connects, fake.contexts), (3, 1))

    def test_service_restart_rebuilds_the_context(self):
        fake = FakePcsc(idle, fail_with(FakePcsc.SCARD_E_NO_SERVICE), recover, idle)
        self.run_agent(fake)
        self.assertEqual((fake.contexts, fake.released), (2, 2))

    def test_unplugged_reader_is_rediscovered(self):
        def unplug(f):
            f.unknown, f.readers = True, []
            return True
        fake = FakePcsc(idle, unplug)
        sleeps = []
        def sleep(seconds):
            sleeps.append(seconds)
            if len(sleeps) == 2:
                fake.unknown, fake.readers = False, [READER]
        with patch.object(agent, "scard", fake), patch.object(agent.time, "sleep", side_effect=sleep):
            with self.assertRaises(KeyboardInterrupt):
                agent.run(self.config, Mock())
        # Reconnect pause, then one "no reader" wait before it is back.
        self.assertEqual(sleeps, [2, 3])
        self.assertEqual(fake.contexts, 2)

    def test_sigterm_unwinds_resource_cleanup(self):
        fake = FakePcsc()
        fake.SCardGetStatusChange = lambda *args: agent.stop_agent(None, None)
        with patch.object(agent, "scard", fake):
            with self.assertRaises(SystemExit) as exit:
                agent.run(self.config, Mock())
        self.assertEqual(exit.exception.code, 0)
        self.assertEqual(fake.released, 1)

    def test_sigterm_during_ack_does_not_wait_for_a_held_card(self):
        fake = FakePcsc(FakePcsc.insert)
        with patch.object(agent, "scard", fake), \
             patch.object(agent, "handle_card_scan", side_effect=lambda *args: agent.stop_agent(None, None)), \
             patch.object(agent.ReaderSession, "wait_for_removal") as wait:
            with self.assertRaises(SystemExit):
                agent.run(self.config, Mock())
        wait.assert_not_called()
        self.assertEqual(fake.released, 1)


if __name__ == "__main__":
    unittest.main()
