"""No runtime or platform dependency: late-join readiness must depend on frames, not bytes."""
import struct
import unittest
import zlib
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


if __name__ == '__main__':
    unittest.main()
