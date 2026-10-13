"""No runtime or platform dependency: late-join readiness must depend on frames, not bytes."""
import struct
import unittest
import zlib
import tempfile
from pathlib import Path
from terminal_fixture import TerminalFrameObserver, wait_for_terminal
from streaming_fixture import choose_pause


def frame(kind, payload):
    head = struct.pack('<II', kind, len(payload))
    return head + struct.pack('<I', zlib.crc32(payload, zlib.crc32(head))) + payload


def capture(event_frames=130):
    header = b'DVNTRC01' + struct.pack('<II', 2, 0) + bytes(48)
    header += struct.pack('<I', zlib.crc32(header))
    return header + frame(1, bytes(32)) + frame(2, bytes(12)) + b''.join(
        frame(3, struct.pack('<I', 1) + bytes(48)) for _ in range(event_frames)) + frame(5, bytes(80))


class StreamingFixtureTests(unittest.TestCase):
    def test_short_recording_has_a_complete_pre_footer_pause(self):
        data = capture()
        point = choose_pause(data)
        self.assertLess(len(data), 2 * 1024 * 1024)
        self.assertGreaterEqual(point.blocks, 100)
        self.assertLess(point.byte_offset, len(data) - 92)
        self.assertEqual(point.final_bytes, len(data))

    def test_insufficient_history_fails_immediately(self):
        with self.assertRaisesRegex(ValueError, 'too few frames'):
            choose_pause(capture(10))

    def test_truncated_corrupt_and_trailing_data_fail(self):
        data = capture()
        for bad in (data[:-1], data + b'x', data[:80] + bytes([data[80] ^ 1]) + data[81:]):
            with self.assertRaises(ValueError):
                choose_pause(bad)

    def test_late_identity_definition_cannot_be_hidden_by_total_count(self):
        data = capture()
        footer = data[-92:]
        data = data[:-92] + frame(2, bytes(12)) + frame(3, struct.pack('<I', 1) + bytes(48)) + footer
        # A valid earlier pause still exists; it never uses bytes after the late identity to claim recovery.
        self.assertLess(choose_pause(data).byte_offset, len(capture()) - 92)


class TerminalFixtureTests(unittest.TestCase):
    def test_every_split_waits_for_complete_checksummed_footer(self):
        data = capture(2)
        for split in range(len(data)):
            observer = TerminalFrameObserver()
            self.assertFalse(observer.feed(data[:split]))
            self.assertTrue(observer.feed(data[split:]))

    def test_bytewise_delivery_including_v4_control_frames(self):
        header = b'DVNTRC01' + struct.pack('<II', 4, 0) + bytes(48)
        data = header + struct.pack('<I', zlib.crc32(header)) + frame(6, bytes(32)) + frame(7, bytes(16)) + frame(5, bytes(80))
        observer = TerminalFrameObserver()
        for byte in data[:-1]:
            self.assertFalse(observer.feed(bytes([byte])))
        self.assertTrue(observer.feed(data[-1:]))

    def test_corruption_is_not_retried_as_temporary_eof(self):
        data = capture(2)
        for offset in range(len(data)):
            observer = TerminalFrameObserver()
            bad = data[:offset] + bytes([data[offset] ^ 64]) + data[offset + 1:]
            try:
                complete = observer.feed(bad)
            except ValueError:
                continue
            self.assertFalse(complete, f'corruption at {offset} was accepted')

    def test_trailing_data_and_invalid_terminal_size_are_rejected(self):
        data = capture(2)
        for bad in (data + b'x', data[:-92] + frame(5, bytes(79))):
            with self.assertRaises(ValueError):
                TerminalFrameObserver().feed(bad)
        observer = TerminalFrameObserver()
        self.assertTrue(observer.feed(data))
        with self.assertRaises(ValueError):
            observer.feed(b'x')

    def test_budget_does_not_grow_with_unfinished_input(self):
        with self.assertRaises(ValueError):
            TerminalFrameObserver(maximum_bytes=160).feed(capture(2))
        with self.assertRaises(ValueError):
            TerminalFrameObserver().feed(bytes(65549))

    def test_quiet_partial_file_times_out_instead_of_succeeding(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'partial.dvntrace'
            path.write_bytes(capture(2)[:-1])
            with self.assertRaises(TimeoutError):
                wait_for_terminal(path, timeout=.02, poll_interval=.001)
            path.write_bytes(capture(2))
            wait_for_terminal(path, timeout=.1)

    def test_invalid_wait_limits_are_rejected(self):
        for timeout in (0, -1, float('nan'), float('inf')):
            with self.assertRaises(ValueError):
                wait_for_terminal(Path('not-read'), timeout=timeout)


if __name__ == '__main__':
    unittest.main()
