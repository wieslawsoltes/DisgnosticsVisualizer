"""Choose a verified frame boundary for late-join tests, independent of runner throughput."""
from dataclasses import dataclass
import struct
import zlib


@dataclass(frozen=True)
class PausePoint:
    byte_offset: int
    blocks: int
    frames: int
    final_bytes: int


def choose_pause(data: bytes, tail_blocks: int = 48) -> PausePoint:
    if len(data) < 68 or len(data) > 128 * 1024 * 1024 or data[:8] != b'DVNTRC01':
        raise ValueError('Invalid reference capture header or file budget')
    if struct.unpack_from('<II', data, 8) not in ((2, 0), (3, 0)) or zlib.crc32(data[:64]) != struct.unpack_from('<I', data, 64)[0]:
        raise ValueError('Invalid reference capture version or header checksum')
    offset, blocks, last_identity = 68, 1, 1
    statuses = {}
    frames = []
    footer = False
    while offset < len(data):
        if footer or len(data) - offset < 12:
            raise ValueError('Trailing or truncated reference frame')
        kind, length, crc = struct.unpack_from('<III', data, offset)
        end = offset + 12 + length
        if kind not in range(1, 6) or length > 65536 or end > len(data):
            raise ValueError('Invalid reference frame extent')
        payload = data[offset + 12:end]
        if zlib.crc32(payload, zlib.crc32(data[offset:offset + 8])) != crc:
            raise ValueError('Invalid reference frame checksum')
        emit = True
        if kind == 4:
            if length != 16:
                raise ValueError('Invalid status frame')
            site = struct.unpack_from('<I', payload)[0]
            emit = statuses.get(site) != payload
            statuses[site] = payload
        blocks += int(emit)
        if kind in (1, 2):
            last_identity = blocks
        frames.append((end, blocks, last_identity, kind))
        footer = kind == 5
        offset = end
    if not footer:
        raise ValueError('Reference recording has no terminal frame')
    # Leave at least one event frame and the footer for the viewer to observe after joining.
    last_event = max((i for i, row in enumerate(frames) if row[3] == 3), default=-1)
    for i, (end, count, identity, kind) in enumerate(frames):
        if i < last_event and kind == 3 and count >= 100 and count - identity > tail_blocks:
            return PausePoint(end, count, len(frames), len(data))
    raise ValueError('Reference recording has too few frames to prove late-join metadata recovery')
