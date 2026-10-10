using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DiagnosticsVisualizer.Protocol;

namespace DiagnosticsVisualizer.Transport;

public static class DatagramLimits
{
    public const uint Magic = 0x31555644; // DVU1
    public const int Mtu = 1200, Header = 36, Tag = 16;
    public const int Payload = Mtu - Header - Tag;
    public const int MaxFrame = 128 * 1024, MaxFragments = 128;
}

/// <summary>AES-256-GCM with a per-session derived key and non-reusable (sequence,index) nonces.</summary>
[UnsupportedOSPlatform("browser")]
public sealed class SecureDatagramEncoder : IDisposable
{
    private readonly Guid _session;
    private readonly AesGcm _cipher;
    private readonly object _gate = new();
    private long _lastSequence = -1;
    public SecureDatagramEncoder(Guid session, ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32 || session == Guid.Empty) throw new ArgumentException("A 256-bit key and nonempty session are required.");
        _session = session; var derived = HMACSHA256.HashData(masterKey, session.ToByteArray());
        _cipher = new(derived, DatagramLimits.Tag); CryptographicOperations.ZeroMemory(derived);
    }
    public IReadOnlyList<byte[]> Encode(long sequence, ReadOnlySpan<byte> frame)
    {
        lock (_gate)
        {
            if (sequence < 0 || sequence <= _lastSequence) throw new InvalidOperationException("Never reuse a datagram sequence with the same session key.");
            if (frame.Length is < 1 or > DatagramLimits.MaxFrame) throw new InvalidDataException("Use the reliable channel for large trace checkpoints.");
            if (WireEnvelope.GetKind(frame) != FrameKind.Trace) throw new InvalidDataException("UDP carries telemetry, not control commands.");
            _lastSequence = sequence;
            var count = (frame.Length + DatagramLimits.Payload - 1) / DatagramLimits.Payload;
            var packets = new List<byte[]>(count); Span<byte> nonce = stackalloc byte[12];
            for (var i = 0; i < count; i++)
            {
                var piece = frame.Slice(i * DatagramLimits.Payload, Math.Min(DatagramLimits.Payload, frame.Length - i * DatagramLimits.Payload));
                var packet = new byte[DatagramLimits.Header + piece.Length + DatagramLimits.Tag];
                BinaryPrimitives.WriteUInt32LittleEndian(packet, DatagramLimits.Magic); _session.TryWriteBytes(packet.AsSpan(4, 16));
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(20), sequence);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), (ushort)i);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(30), (ushort)count);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(32), (ushort)piece.Length);
                BinaryPrimitives.WriteInt64LittleEndian(nonce, sequence); BinaryPrimitives.WriteInt32LittleEndian(nonce[8..], i);
                _cipher.Encrypt(nonce, piece, packet.AsSpan(DatagramLimits.Header, piece.Length),
                    packet.AsSpan(packet.Length - DatagramLimits.Tag), packet.AsSpan(0, DatagramLimits.Header));
                packets.Add(packet);
            }
            return packets;
        }
    }
    public void Dispose() => _cipher.Dispose();
}

/// <summary>One authenticated source session; bounded, expiring out-of-order reassembly and replay window.</summary>
[UnsupportedOSPlatform("browser")]
public sealed class SecureDatagramAssembler : IDisposable
{
    private sealed class Pending(int count)
    {
        public readonly byte[]?[] Parts = new byte[count][];
        public readonly long Created = Stopwatch.GetTimestamp();
        public int Received, Bytes;
    }
    private readonly Guid _session;
    private readonly AesGcm _cipher;
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly object _gate = new();
    private readonly int _maxPending;
    private readonly long _maxBytes;
    private readonly TimeSpan _timeout;
    private long _buffered, _highest = -1;
    private ulong _replay;
    public long RejectedPackets { get; private set; }
    public long ExpiredFrames { get; private set; }
    public long DuplicatePackets { get; private set; }
    public SecureDatagramAssembler(Guid authenticatedSession, ReadOnlySpan<byte> masterKey,
        int maxPending = 32, long maxBytes = 4 * 1024 * 1024, TimeSpan? timeout = null)
    {
        if (masterKey.Length != 32 || authenticatedSession == Guid.Empty || maxPending < 1 || maxBytes < DatagramLimits.MaxFrame)
            throw new ArgumentException("Invalid datagram receiver limits or credentials.");
        _session = authenticatedSession; _maxPending = maxPending; _maxBytes = maxBytes; _timeout = timeout ?? TimeSpan.FromSeconds(2);
        var key = HMACSHA256.HashData(masterKey, authenticatedSession.ToByteArray()); _cipher = new(key, DatagramLimits.Tag);
        CryptographicOperations.ZeroMemory(key);
    }
    public bool TryAccept(ReadOnlySpan<byte> packet, out byte[]? completed)
    {
        lock (_gate)
        {
            completed = null;
            if (packet.Length is < DatagramLimits.Header + DatagramLimits.Tag or > DatagramLimits.Mtu ||
                BinaryPrimitives.ReadUInt32LittleEndian(packet) != DatagramLimits.Magic || new Guid(packet.Slice(4, 16)) != _session ||
                packet[34] != 0 || packet[35] != 0) return Reject();
            var sequence = BinaryPrimitives.ReadInt64LittleEndian(packet[20..]);
            var index = BinaryPrimitives.ReadUInt16LittleEndian(packet[28..]);
            var count = BinaryPrimitives.ReadUInt16LittleEndian(packet[30..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(packet[32..]);
            if (sequence < 0 || count is < 1 or > DatagramLimits.MaxFragments || index >= count || size is < 1 or > DatagramLimits.Payload ||
                size != packet.Length - DatagramLimits.Header - DatagramLimits.Tag || (index < count - 1 && size != DatagramLimits.Payload)) return Reject();
            Span<byte> nonce = stackalloc byte[12]; BinaryPrimitives.WriteInt64LittleEndian(nonce, sequence); BinaryPrimitives.WriteInt32LittleEndian(nonce[8..], index);
            Span<byte> plain = stackalloc byte[DatagramLimits.Payload];
            try { _cipher.Decrypt(nonce, packet.Slice(DatagramLimits.Header, size), packet[^DatagramLimits.Tag..], plain[..size], packet[..DatagramLimits.Header]); }
            catch (CryptographicException) { return Reject(); }
            if (_highest >= sequence && (_highest - sequence >= 64 || (_replay & (1UL << (int)(_highest - sequence))) != 0))
            { DuplicatePackets++; return false; }
            Expire();
            if (!_pending.TryGetValue(sequence, out var state))
            {
                while (_pending.Count >= _maxPending) EvictOldest();
                _pending[sequence] = state = new(count);
            }
            if (state.Parts.Length != count) return Reject();
            if (state.Parts[index] is not null) { DuplicatePackets++; return false; }
            if (state.Bytes + size > DatagramLimits.MaxFrame || _buffered + size > _maxBytes)
            { Remove(sequence); ExpiredFrames++; return false; }
            state.Parts[index] = plain[..size].ToArray(); state.Received++; state.Bytes += size; _buffered += size;
            if (state.Received != count) return false;
            var frame = new byte[state.Bytes]; var offset = 0;
            foreach (var part in state.Parts) { part!.CopyTo(frame, offset); offset += part.Length; }
            Remove(sequence); MarkComplete(sequence);
            try
            {
                if (WireEnvelope.GetKind(frame) != FrameKind.Trace || frame.Length < 32 || new Guid(frame.AsSpan(WireEnvelope.HeaderBytes, 16)) != _session) return Reject();
            }
            catch (InvalidDataException) { return Reject(); }
            completed = frame; return true;
        }
    }
    private bool Reject() { RejectedPackets++; return false; }
    private void Remove(long sequence) { if (_pending.Remove(sequence, out var state)) _buffered -= state.Bytes; }
    private void Expire()
    {
        foreach (var entry in _pending.Where(p => Stopwatch.GetElapsedTime(p.Value.Created) > _timeout).Select(p => p.Key).ToArray())
        { Remove(entry); ExpiredFrames++; }
    }
    private void EvictOldest()
    { if (_pending.Count != 0) { Remove(_pending.MinBy(p => p.Value.Created).Key); ExpiredFrames++; } }
    private void MarkComplete(long sequence)
    {
        if (_highest < 0) { _highest = sequence; _replay = 1; }
        else if (sequence > _highest)
        { var shift = sequence - _highest; _replay = shift >= 64 ? 1 : (_replay << (int)shift) | 1; _highest = sequence; }
        else _replay |= 1UL << (int)(_highest - sequence);
    }
    public void Dispose() { lock (_gate) { _pending.Clear(); _cipher.Dispose(); _buffered = 0; } }
}
