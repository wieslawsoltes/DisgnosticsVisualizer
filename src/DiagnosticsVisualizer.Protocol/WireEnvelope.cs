using System.Buffers.Binary;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

public enum FrameKind : byte { Trace = 1, Command = 2, SnapshotDelta = 3 }

public static class WireEnvelope
{
    public const uint Magic = 0x52545641; // AVTR
    public const int HeaderBytes = 20;
    internal static byte[] Seal(FrameKind kind, ReadOnlySpan<byte> payload)
    {
        var length = checked(HeaderBytes + payload.Length + 4);
        if (length > TraceLimits.MaxFrameBytes) throw new InvalidDataException("Frame too large.");
        var result = new byte[length]; var s = result.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic); s[4] = 1; s[5] = (byte)kind;
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], HeaderBytes);
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], length);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], payload.Length);
        payload.CopyTo(s[HeaderBytes..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[^4..], Crc32.Compute(s[..^4]));
        return result;
    }
    public static int ReadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || header[4] != 1 ||
            header[5] is < 1 or > 3 || BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != 0)
            throw new InvalidDataException("Unsupported trace envelope.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (length < HeaderBytes + 4 || length > TraceLimits.MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        return length;
    }
    public static FrameKind GetKind(ReadOnlySpan<byte> frame) { Validate(frame); return (FrameKind)frame[5]; }
    internal static ReadOnlySpan<byte> Open(ReadOnlySpan<byte> frame, FrameKind expected)
    {
        Validate(frame);
        if ((FrameKind)frame[5] != expected) throw new InvalidDataException("Unexpected frame kind.");
        return frame.Slice(HeaderBytes, frame.Length - HeaderBytes - 4);
    }
    public static void Validate(ReadOnlySpan<byte> frame)
    {
        var length = ReadLength(frame);
        if (frame.Length != length || BinaryPrimitives.ReadInt32LittleEndian(frame[12..]) != length - HeaderBytes - 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(frame[^4..]) != Crc32.Compute(frame[..^4]))
            throw new InvalidDataException("Truncated, corrupt, or concatenated frame.");
    }
}

public static class Crc32
{
    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) { var c = i; for (var b = 0; b < 8; b++) c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1; table[i] = c; }
        return table;
    }
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var b in data) crc = Table[(crc ^ b) & 255] ^ (crc >> 8);
        return ~crc;
    }
}
