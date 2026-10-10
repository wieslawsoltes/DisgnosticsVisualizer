using System.Buffers.Binary;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

public enum FrameKind : byte { Trace = 1, Command = 2 }

/// <summary>Little-endian 12-byte header, bounded payload, CRC32 trailer.</summary>
public static class WireEnvelope
{
    public const uint Magic = 0x31545644; // DVT1
    public const int HeaderBytes = 12;
    internal static byte[] Seal(FrameKind kind, ReadOnlySpan<byte> payload)
    {
        var length = checked(HeaderBytes + payload.Length + 4);
        if (length > TraceLimits.MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        bytes[4] = 1; bytes[5] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), length);
        payload.CopyTo(bytes.AsSpan(HeaderBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length - 4), Crc32.Compute(bytes.AsSpan(0, length - 4)));
        return bytes;
    }
    public static FrameKind GetKind(ReadOnlySpan<byte> frame)
    {
        Validate(frame); return (FrameKind)frame[5];
    }
    internal static ReadOnlySpan<byte> Open(ReadOnlySpan<byte> frame, FrameKind kind)
    {
        Validate(frame);
        if (frame[5] != (byte)kind) throw new InvalidDataException("Unexpected frame kind.");
        return frame[HeaderBytes..^4];
    }
    public static int GetFrameLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
            header[4] != 1 || header[6] != 0 || header[7] != 0 || header[5] is < 1 or > 2)
            throw new InvalidDataException("Unsupported trace header/version.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (length < HeaderBytes + 4 || length > TraceLimits.MaxFrameBytes)
            throw new InvalidDataException("Invalid frame length.");
        return length;
    }
    public static void Validate(ReadOnlySpan<byte> frame)
    {
        var length = GetFrameLength(frame);
        if (frame.Length != length) throw new InvalidDataException("Truncated or oversized frame.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame[^4..]) != Crc32.Compute(frame[..^4]))
            throw new InvalidDataException("Trace checksum mismatch.");
    }
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderBytes];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = GetFrameLength(header);
        var frame = new byte[length]; header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(HeaderBytes), cancellationToken).ConfigureAwait(false);
        Validate(frame); return frame;
    }
}
