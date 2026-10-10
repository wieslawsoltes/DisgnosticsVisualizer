using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

internal sealed class WireWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;
    private void Reserve(int bytes)
    { if (bytes < 0 || _buffer.WrittenCount > TraceLimits.MaxFrameBytes - WireEnvelope.HeaderBytes - 4 - bytes) throw new InvalidDataException("Frame too large."); }
    public void Byte(byte value) { Reserve(1); var s = _buffer.GetSpan(1); s[0] = value; _buffer.Advance(1); }
    public void Bytes(ReadOnlySpan<byte> value) { Reserve(value.Length); value.CopyTo(_buffer.GetSpan(value.Length)); _buffer.Advance(value.Length); }
    public void UInt(uint value) { Reserve(4); var s = _buffer.GetSpan(4); BinaryPrimitives.WriteUInt32LittleEndian(s, value); _buffer.Advance(4); }
    public void Double(double value) => U64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
    public void U64(ulong value)
    {
        Reserve(10); var s = _buffer.GetSpan(10); var n = 0;
        while (value >= 128) { s[n++] = (byte)(value | 128); value >>= 7; }
        s[n++] = (byte)value; _buffer.Advance(n);
    }
    public void I64(long value) => U64(unchecked((ulong)((value << 1) ^ (value >> 63))));
    public void Count(int count, int max)
    {
        if (count < 0 || count > max) throw new InvalidDataException($"Count exceeds {max}.");
        U64((ulong)count);
    }
    public void Text(string text)
    {
        var size = Encoding.UTF8.GetByteCount(text);
        Count(size, TraceLimits.MaxStringBytes);
        Reserve(size); var n = Encoding.UTF8.GetBytes(text.AsSpan(), _buffer.GetSpan(size)); _buffer.Advance(n);
    }
}

internal ref struct WireReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _remaining = data;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public int Remaining => _remaining.Length;
    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > _remaining.Length) throw new InvalidDataException("Truncated field.");
        var value = _remaining[..count]; _remaining = _remaining[count..]; return value;
    }
    public byte Byte() => Bytes(1)[0];
    public bool Bool() => Byte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid boolean.") };
    public ulong U64()
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = Byte();
            if (shift == 63 && b > 1) throw new InvalidDataException("Varint overflow.");
            value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid varint.");
    }
    public long I64() { var u = U64(); return unchecked((long)(u >> 1) ^ -((long)u & 1)); }
    public int Int()
    {
        var value = I64();
        if (value is < int.MinValue or > int.MaxValue) throw new InvalidDataException("Integer overflow.");
        return (int)value;
    }
    public long NonNegative()
    {
        var value = U64();
        if (value > long.MaxValue) throw new InvalidDataException("Unsigned value exceeds Int64.");
        return (long)value;
    }
    public int Count(int max)
    {
        var value = U64();
        if (value > (ulong)max) throw new InvalidDataException($"Count exceeds {max}.");
        return (int)value;
    }
    public double Double() => BitConverter.Int64BitsToDouble(unchecked((long)U64()));
    public string Text()
    {
        var bytes = Bytes(Count(TraceLimits.MaxStringBytes));
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8.", e); }
    }
    public void Finish() { if (!_remaining.IsEmpty) throw new InvalidDataException("Unexpected trailing fields."); }
}

public static class Crc32
{
    private static readonly uint[] Table = CreateTable();
    private static uint[] CreateTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < t.Length; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data) crc = Table[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
}
