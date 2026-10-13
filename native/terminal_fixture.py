"""Wait for native recorder finalization before a test permits its target process to exit."""
import math
import struct
import time
import zlib


class TerminalFrameObserver:
    """Incremental bounded validation: a partial append is not a terminal recording."""
    def __init__(self, maximum_bytes=128 * 1024 * 1024):
        if maximum_bytes < 160:
            raise ValueError('Invalid capture budget')
        self.maximum_bytes = maximum_bytes
        self.total = 0
        self.pending = bytearray()
        self.header_read = False
        self.terminal = False

    def feed(self, data):
        if len(data) > 65548 or self.total + len(data) > self.maximum_bytes:
            raise ValueError('Native terminal observation exceeded its bounded input budget')
        if self.terminal and data:
            raise ValueError('Bytes follow the native terminal frame')
        self.total += len(data)
        self.pending.extend(data)
        if not self.header_read:
            if len(self.pending) < 68:
                return False
            header = self.pending[:68]
            if header[:8] != b'DVNTRC01' or struct.unpack_from('<II', header, 8) not in ((2, 0), (3, 0), (4, 0)) or zlib.crc32(header[:64]) != struct.unpack_from('<I', header, 64)[0]:
                raise ValueError('Invalid native capture header')
            del self.pending[:68]
            self.header_read = True
        while len(self.pending) >= 12:
            kind, length, crc = struct.unpack_from('<III', self.pending)
            if kind not in range(1, 8) or length > 65536:
                raise ValueError('Invalid native frame extent')
            if len(self.pending) < 12 + length:
                break
            payload = self.pending[12:12 + length]
            if zlib.crc32(payload, zlib.crc32(self.pending[:8])) != crc:
                raise ValueError('Invalid native frame checksum')
            del self.pending[:12 + length]
            if kind == 5:
                if length != 80 or self.pending:
                    raise ValueError('Invalid or nonfinal terminal frame')
                self.terminal = True
                return True
        return self.terminal


def wait_for_terminal(path, timeout=5.0, poll_interval=.01):
    if not math.isfinite(timeout) or timeout <= 0 or not math.isfinite(poll_interval) or poll_interval <= 0:
        raise ValueError('Invalid terminal observation deadline')
    deadline = time.monotonic() + timeout
    observer = TerminalFrameObserver()
    with path.open('rb') as source:
        while time.monotonic() < deadline:
            block = source.read(16384)
            if observer.feed(block):
                # Detect bytes already present after the footer, without waiting for another append.
                observer.feed(source.read(1))
                return
            if not block:
                time.sleep(min(poll_interval, max(0, deadline - time.monotonic())))
    raise TimeoutError('Native recorder did not publish a validated terminal frame before the deadline')
