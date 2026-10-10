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
    public void Byte(byte value) { Reserve(1); _buffer.GetSpan(1)[0] = value; _buffer.Advance(1); }
    public void Bytes(ReadOnlySpan<byte> value) { Reserve(value.Length); value.CopyTo(_buffer.GetSpan(value.Length)); _buffer.Advance(value.Length); }
    public void UInt(uint value) { Reserve(4); BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), value); _buffer.Advance(4); }
    public void U64(ulong value)
    {
        Reserve(10); var s = _buffer.GetSpan(10); var n = 0;
        while (value >= 128) { s[n++] = (byte)(value | 128); value >>= 7; }
        s[n++] = (byte)value; _buffer.Advance(n);
    }
    public void I64(long value) => U64(unchecked((ulong)((value << 1) ^ (value >> 63))));
    public void NonNegative(long value) { if (value < 0) throw new InvalidDataException("Negative unsigned field."); U64((ulong)value); }
    public void Count(int count, int maximum) { if (count < 0 || count > maximum) throw new InvalidDataException("Collection limit exceeded."); U64((uint)count); }
    public void Text(string? text)
    {
        text ??= ""; var size = Encoding.UTF8.GetByteCount(text);
        if (size > TraceLimits.MaxStringBytes) throw new InvalidDataException("String limit exceeded.");
        U64((uint)size);
        Reserve(size); var n = Encoding.UTF8.GetBytes(text.AsSpan(), _buffer.GetSpan(size)); _buffer.Advance(n);
    }
    public void Double(double value) => I64(BitConverter.DoubleToInt64Bits(value));
}

internal ref struct WireReader(ReadOnlySpan<byte> source)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private ReadOnlySpan<byte> _remaining = source;
    public bool End => _remaining.IsEmpty;
    public byte Byte() { if (_remaining.IsEmpty) throw new InvalidDataException("Unexpected end of frame."); var value = _remaining[0]; _remaining = _remaining[1..]; return value; }
    public ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > _remaining.Length) throw new InvalidDataException("Truncated field.");
        var result = _remaining[..count]; _remaining = _remaining[count..]; return result;
    }
    public ulong U64()
    {
        ulong value = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = Byte();
            if (i == 9 && (b & 254) != 0) throw new InvalidDataException("Varint overflow.");
            value |= (ulong)(b & 127) << (i * 7);
            if ((b & 128) == 0)
            {
                if (i > 0 && b == 0) throw new InvalidDataException("Non-canonical varint.");
                return value;
            }
        }
        throw new InvalidDataException("Unterminated varint.");
    }
    public long I64() { var u = U64(); return unchecked((long)(u >> 1) ^ -((long)u & 1)); }
    public long NonNegative() { var u = U64(); if (u > long.MaxValue) throw new InvalidDataException("Unsigned field overflow."); return (long)u; }
    public int Count(int maximum) { var u = U64(); if (u > (uint)maximum) throw new InvalidDataException("Collection limit exceeded."); return (int)u; }
    public string Text() { var count = Count(TraceLimits.MaxStringBytes); try { return Utf8.GetString(Bytes(count)); } catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid UTF-8.", e); } }
    public double Double() => BitConverter.Int64BitsToDouble(I64());
    public bool Bool() => Byte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid Boolean.") };
    public void Finish() { if (!End) throw new InvalidDataException("Unexpected trailing payload."); }
}

public static class CommandCodec
{
    public static byte[] Encode(TraceCommand command)
    {
        var w = new WireWriter(); w.I64(command.RequestId); w.Byte((byte)command.Kind); w.I64(command.ObjectId); w.Text(command.Argument);
        return WireEnvelope.Seal(FrameKind.Command, w.Written);
    }
    public static TraceCommand Decode(ReadOnlySpan<byte> frame)
    {
        var r = new WireReader(WireEnvelope.Open(frame, FrameKind.Command));
        var result = new TraceCommand(r.I64(), (CommandKind)r.Byte(), r.I64(), r.Text()); r.Finish();
        if (!Enum.IsDefined(result.Kind)) throw new InvalidDataException("Unknown command.");
        return result;
    }
}
