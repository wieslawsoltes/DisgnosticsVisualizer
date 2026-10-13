using System.Buffers.Binary;
using System.Text;
using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

/// <summary>Bounded streaming reader for native ReJIT recordings. Does not execute code, resolve paths, or attach a process.</summary>
public static class NativeTraceFile
{
    public const int HeaderBytes = 68, MaximumFrameBytes = 65536;
    public const long MaximumFileBytes = 128L * 1024 * 1024;
    public static bool IsMagic(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual("DVNTRC01"u8);
    private sealed class State(Guid session, long frequency, long origin, long utc, bool follow)
    {
        public readonly Dictionary<int, Symbol> Sites = [];
        public readonly Dictionary<int, NativeThreadInfo> Threads = [];
        public readonly Dictionary<int, NativeMethodInfo> Methods = [];
        public readonly HashSet<long> EventIds = [];
        public long Sequence, Records, Bytes = HeaderBytes, LastEnd;
        public int MetadataId = 1_000_000;
        public NativeCaptureEnd? Footer;
        public TraceBlock Block(TraceEvent[]? events = null, Symbol[]? symbols = null) => new()
        {
            SessionId = session, Sequence = ++Sequence, Frequency = frequency, OriginTimestamp = origin, UtcStartTicks = utc,
            Recording = follow && Footer is null, Events = events ?? [], Symbols = symbols ?? [], DroppedEvents = Footer?.DroppedRecords ?? 0, ProbeFaults = Footer?.RecorderFaults ?? 0
        };
        public Symbol Symbol(NativeMetadata entry)
        {
            if (MetadataId >= 1_100_000) throw new InvalidDataException("Native metadata update budget exceeded.");
            return new(MetadataId++, NativeMetadataSymbol.Encode(entry));
        }
        public TraceBlock? Frame(uint kind, byte[] payload)
        {
            var r = new Reader(payload);
            switch (kind)
            {
                case 1:
                    var id = r.Id(4096); var mvid = new Guid(r.Bytes(16)); var token = r.Int32();
                    var site = new SourceSite(r.Text(), mvid, token, r.Text(), "", 0, 0, 0, 0, "", "", "", false);
                    var symbol = new Symbol(id, SourceSiteSymbol.Encode(site));
                    if (!Sites.TryAdd(id, symbol)) throw new InvalidDataException("Duplicate native method identity.");
                    r.Finish(); return Block(symbols: [symbol]);
                case 2:
                    var thread = new NativeThreadInfo(r.Id(64), r.UInt64());
                    if (thread.OsThreadId == 0 || !Threads.TryAdd(thread.Slot, thread)) throw new InvalidDataException("Duplicate or invalid native thread.");
                    r.Finish(); return Block(symbols: [Symbol(thread)]);
                case 3:
                    var count = r.Id(256); var events = new TraceEvent[count];
                    Records = checked(Records + count);
                    if (Records > 3_000_000) throw new InvalidDataException("Native record budget exceeded.");
                    for (var n = 0; n < count; n++)
                    {
                        var cookie = r.Int64(); var parent = r.Int64(); var timestamp = r.Int64(); var duration = r.Int64();
                        var method = r.Id(4096); var slot = r.Id(64); var phase = r.UInt32(); var flags = r.UInt32();
                        if (!Sites.ContainsKey(method) || !Threads.ContainsKey(slot) || phase > 2 || flags != 0 || cookie > long.MaxValue / 2 - 1 || parent > long.MaxValue / 2 - 1)
                            throw new InvalidDataException("Invalid native event identity or phase.");
                        var e = new TraceEvent(cookie * 2 + (phase == 0 ? 0 : 1), parent == 0 ? 0 : parent * 2 + 1, timestamp, duration,
                            0, 0, slot, method, Subsystem.Application, phase == 0 ? TraceEventKind.Link : TraceEventKind.Span,
                            phase == 0 ? NativeMethodEvent.BeginCode : phase == 1 ? NativeMethodEvent.ReturnedCode : NativeMethodEvent.UnwoundCode, cookie);
                        if (!NativeMethodEvent.IsValid(e) || e.Timestamp < origin || !EventIds.Add(e.Id)) throw new InvalidDataException("Invalid or duplicate native event.");
                        events[n] = e; LastEnd = Math.Max(LastEnd, e.End);
                    }
                    r.Finish(); return Block(events, events.Select(e => e.NameId).Distinct().Select(id => Sites[id]).ToArray());
                case 4:
                    var methodInfo = new NativeMethodInfo(r.Id(4096), r.Int32(), r.Int32(), r.UInt32());
                    if (!Sites.ContainsKey(methodInfo.SiteId) || methodInfo.Status is < -7 or > 2) throw new InvalidDataException("Invalid native method status.");
                    r.Finish();
                    if (Methods.GetValueOrDefault(methodInfo.SiteId) == methodInfo) return null;
                    Methods[methodInfo.SiteId] = methodInfo; return Block(symbols: [Symbol(methodInfo)]);
                case 5:
                    var reason = r.UInt32(); if (reason > 3 || r.UInt32() != 0) throw new InvalidDataException("Invalid native stop reason.");
                    Footer = new(true, false, (int)reason, r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), Records, Bytes);
                    if (Footer.EndTimestamp < Math.Max(origin, LastEnd)) throw new InvalidDataException("Native capture ended before its events.");
                    NativeMetadataSymbol.Encode(Footer); r.Finish(); return Block(symbols: [Symbol(Footer)]);
                default: throw new InvalidDataException("Unknown native frame kind.");
            }
        }
    }
    public static Task<TraceReadResult> ReadAsync(Stream stream, Func<TraceBlock, ValueTask> consume,
        bool recoverTruncatedTail = true, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(stream, consume, recoverTruncatedTail, null, cancellationToken);

    /// <summary>Follow a growing native file until its validated terminal frame. EOF is temporary; stalls fail explicitly.</summary>
    public static Task<TraceReadResult> FollowAsync(Stream stream, Func<TraceBlock, ValueTask> consume,
        TimeSpan idleTimeout, CancellationToken cancellationToken = default)
    {
        if (idleTimeout < TimeSpan.FromMilliseconds(100) || idleTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        return ReadCoreAsync(stream, consume, false, idleTimeout, cancellationToken);
    }
    private static async Task<TraceReadResult> ReadCoreAsync(Stream stream, Func<TraceBlock, ValueTask> consume,
        bool recoverTruncatedTail, TimeSpan? idleTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream); ArgumentNullException.ThrowIfNull(consume);
        if (!stream.CanRead) throw new ArgumentException("Readable stream required.", nameof(stream));
        var header = new byte[HeaderBytes];
        if (await ReadAvailable(stream, header.AsMemory(0, 64), idleTimeout, cancellationToken).ConfigureAwait(false) != 64 || !IsMagic(header))
            throw new InvalidDataException("Invalid or truncated native header.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (version is < 1 or > 2) throw new InvalidDataException("Unsupported native capture version.");
        // v1 was the pre-release 64-byte header. v2 adds a checksum; it is never guessed by consuming a frame prefix.
        if (version == 2 && (await ReadAvailable(stream, header.AsMemory(64, 4), idleTimeout, cancellationToken).ConfigureAwait(false) != 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64)) != Crc32.Compute(header.AsSpan(0, 64))))
            throw new InvalidDataException("Invalid or truncated native header checksum.");
        var r = new Reader(header.AsSpan(12, 52));
        if (r.UInt32() != 0) throw new InvalidDataException("Unsupported native capture flags.");
        var frequency = r.Int64(); var origin = r.Int64(); var utc = r.Int64(); var session = new Guid(r.Bytes(16));
        var process = new NativeProcessInfo(r.Id(int.MaxValue), r.Id(8), (int)version); r.Finish();
        if (frequency != 1_000_000_000 || utc > DateTime.MaxValue.Ticks || session == Guid.Empty || process.PointerBytes != 8)
            throw new InvalidDataException("Invalid native source clock or identity.");
        var state = new State(session, frequency, origin, utc, idleTimeout is not null) { Bytes = version == 2 ? HeaderBytes : 64 };
        await consume(state.Block(symbols: [state.Symbol(process)])).ConfigureAwait(false);
        var truncated = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameHeader = new byte[12];
            var read = await ReadAvailable(stream, frameHeader, state.Footer is null ? idleTimeout : null, cancellationToken).ConfigureAwait(false); state.Bytes += read;
            if (state.Footer is not null) { if (read != 0) throw new InvalidDataException("Trailing data after native footer."); break; }
            if (read < frameHeader.Length) { truncated = true; break; }
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader); var size = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.AsSpan(4));
            if (kind is < 1 or > 5 || size > MaximumFrameBytes || state.Bytes > MaximumFileBytes - size)
                throw new InvalidDataException("Invalid or oversized native frame.");
            var payload = new byte[(int)size]; read = await ReadAvailable(stream, payload, idleTimeout, cancellationToken).ConfigureAwait(false); state.Bytes += read;
            if (read != payload.Length) { truncated = true; break; }
            if (BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.AsSpan(8)) != Crc32.Compute(payload, Crc32.Compute(frameHeader.AsSpan(0, 8))))
                throw new InvalidDataException("Native frame checksum mismatch.");
            if (state.Frame(kind, payload) is { } block) await consume(block).ConfigureAwait(false);
        }
        if (truncated && !recoverTruncatedTail) throw new InvalidDataException("Native recording has no complete footer.");
        foreach (var chunk in state.Sites.Keys.Where(id => !state.Methods.ContainsKey(id)).Select(id => state.Symbol(new NativeMethodInfo(id, 0, 0, 0))).Chunk(256))
            await consume(state.Block(symbols: chunk)).ConfigureAwait(false);
        if (truncated) await consume(state.Block(symbols: [state.Symbol(new NativeCaptureEnd(false, true, 4, state.LastEnd, 0, 0, 0, 0, 0, 0, 0, 0, state.Records, state.Bytes))])).ConfigureAwait(false);
        return new(state.Sequence, state.Records, truncated);
    }
    private static async Task<int> ReadAvailable(Stream stream, Memory<byte> buffer, TimeSpan? idleTimeout, CancellationToken ct)
    {
        var read = 0; var progress = Stopwatch.GetTimestamp();
        while (read < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n > 0) { read += n; progress = Stopwatch.GetTimestamp(); continue; }
            if (idleTimeout is null) break;
            if (Stopwatch.GetElapsedTime(progress) >= idleTimeout) throw new TimeoutException("Native capture stalled without a terminal frame.");
            await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
        }
        return read;
    }
    private ref struct Reader(ReadOnlySpan<byte> remaining)
    {
        private ReadOnlySpan<byte> _remaining = remaining;
        public ReadOnlySpan<byte> Bytes(int count)
        { if (count < 0 || _remaining.Length < count) throw new InvalidDataException("Truncated native fields."); var result = _remaining[..count]; _remaining = _remaining[count..]; return result; }
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Bytes(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        public long Int64() { var n = UInt64(); if (n > long.MaxValue) throw new InvalidDataException("Native integer exceeds supported range."); return (long)n; }
        public int Id(int maximum) { var n = UInt32(); if (n == 0 || n > maximum) throw new InvalidDataException("Native identity outside budget."); return (int)n; }
        public string Text()
        {
            var count = Id(8192);
            try { var text = new UTF8Encoding(false, true).GetString(Bytes(count)); if (text.Contains('\0')) throw new InvalidDataException("Null in native metadata."); return text; }
            catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid native UTF-8.", e); }
        }
        public void Finish() { if (!_remaining.IsEmpty) throw new InvalidDataException("Trailing native fields."); }
    }
}

/// <summary>Autodetects standard and native files, including non-seekable uploads; owns neither input nor consumer.</summary>
public static class TraceImport
{
    public static async Task<TraceReadResult> ReadAsync(Stream stream, Func<TraceBlock, ValueTask> consume,
        bool recoverTruncatedTail = true, CancellationToken cancellationToken = default)
    {
        var prefix = new byte[8]; var read = 0;
        while (read < prefix.Length) { var n = await stream.ReadAsync(prefix.AsMemory(read), cancellationToken).ConfigureAwait(false); if (n == 0) break; read += n; }
        if (read < prefix.Length) throw new InvalidDataException("The recording header is empty or truncated.");
        using var wrapped = new PrefixStream(stream, prefix.AsMemory(0, read));
        return NativeTraceFile.IsMagic(prefix.AsSpan(0, read))
            ? await NativeTraceFile.ReadAsync(wrapped, consume, recoverTruncatedTail, cancellationToken).ConfigureAwait(false)
            : await TraceFile.ReadAsync(wrapped, consume, recoverTruncatedTail, cancellationToken).ConfigureAwait(false);
    }
    private sealed class PrefixStream(Stream input, ReadOnlyMemory<byte> prefix) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        { if (_prefix.IsEmpty) return input.Read(buffer); var n = Math.Min(buffer.Length, _prefix.Length); _prefix.Span[..n].CopyTo(buffer); _prefix = _prefix[n..]; return n; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return _prefix.IsEmpty ? input.ReadAsync(buffer, cancellationToken) : ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
