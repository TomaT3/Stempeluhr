"""PC/SC lifecycle regressions, without a card reader or daemon."""
import unittest
from unittest.mock import Mock, patch

# Also works in CI without pyscard installed.
import test_local_scan_server  # noqa: F401
import stempeluhr_nfc_agent as agent


class ReaderTests(unittest.TestCase):
    def test_idle_polls_reuse_context_and_reader(self):
        connection = Mock()
        connection.connect.side_effect = agent.NoCardException()
        reader = Mock()
        reader.createConnection.return_value = connection
        session = agent.ReaderSession("ACR122")
        with patch.object(agent, "select_reader", return_value=reader) as select:
            for _ in range(1000):
                self.assertTrue(session.open())
                self.assertIsNone(agent.read_uid(session.connection))
            select.assert_called_once_with("ACR122")
        reader.createConnection.assert_called_once()
        connection.release.assert_not_called()
        session.close()
        session.close()
        connection.release.assert_called_once()

    def test_card_handles_close_on_success_bad_response_and_transmit_error(self):
        for result in (([1, 2], 0x90, 0), ([], 0x63, 0), agent.CardConnectionException()):
            with self.subTest(result=result):
                connection = Mock()
                if isinstance(result, Exception):
                    connection.transmit.side_effect = result
                    with self.assertRaises(agent.CardConnectionException):
                        agent.read_uid(connection)
                else:
                    connection.transmit.return_value = result
                    self.assertEqual(agent.read_uid(connection), "0102" if result[1] == 0x90 else None)
                connection.disconnect.assert_called_once()
                connection.release.assert_not_called()

    def test_service_error_releases_context_and_rediscovers_reader(self):
        broken, replacement = Mock(), Mock()
        broken.connect.side_effect = agent.CardConnectionException()
        # Even failed disconnect must not prevent release.
        broken.disconnect.side_effect = agent.CardConnectionException()
        replacement.connect.side_effect = agent.NoCardException()
        readers = [Mock(), Mock()]
        readers[0].createConnection.return_value = broken
        readers[1].createConnection.return_value = replacement
        config = agent.AgentConfig("https://kiosk.test", "test", 3, None)
        with patch.object(agent, "select_reader", side_effect=readers) as select, \
             patch.object(agent.time, "sleep", side_effect=[None, KeyboardInterrupt()]):
            with self.assertRaises(KeyboardInterrupt):
                agent.run(config, Mock())
        self.assertEqual(select.call_count, 2)
        broken.release.assert_called_once()
        replacement.release.assert_called_once()

    def test_same_card_is_published_again_only_after_removal_and_debounce(self):
        connection = Mock()
        reader = Mock()
        reader.createConnection.return_value = connection
        config = agent.AgentConfig("https://kiosk.test", "test", 3, None)
        # First held card; removal; same card before debounce; then after debounce.
        with patch.object(agent, "select_reader", return_value=reader), \
             patch.object(agent, "read_uid", side_effect=["A", "A", None, "A", "A", None, KeyboardInterrupt()]), \
             patch.object(agent.time, "monotonic", side_effect=[10, 11, 14]), \
             patch.object(agent.time, "sleep"), \
             patch.object(agent, "handle_card_scan", return_value="dropped") as publish:
            with self.assertRaises(KeyboardInterrupt):
                agent.run(config, Mock())
        self.assertEqual(publish.call_count, 2)
        reader.createConnection.assert_called_once()
        connection.release.assert_called_once()

    def test_sigterm_unwinds_resource_cleanup(self):
        connection, reader = Mock(), Mock()
        reader.createConnection.return_value = connection
        config = agent.AgentConfig("https://kiosk.test", "test", 3, None)
        with patch.object(agent, "select_reader", return_value=reader), \
             patch.object(agent, "read_uid", side_effect=lambda _: agent.stop_agent(None, None)):
            with self.assertRaises(SystemExit) as exit:
                agent.run(config, Mock())
        self.assertEqual(exit.exception.code, 0)
        connection.release.assert_called_once()


if __name__ == "__main__":
    unittest.main()
