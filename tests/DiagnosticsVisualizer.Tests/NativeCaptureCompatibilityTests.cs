using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class NativeCaptureCompatibilityTests
{
    internal static byte[] Recording(int version = 2, bool footer = true)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write("DVNTRC01"u8); w.Write(version); w.Write(0); w.Write(1_000_000_000L); w.Write(1000L); w.Write(638_000_000_000_000_000L);
        w.Write(Guid.Parse("55555555-1234-5678-9012-555555555555").ToByteArray()); w.Write(123); w.Write(8);
        if (version >= 2) w.Write(Crc32.Compute(stream.ToArray()));
        Frame(1, p => { p.Write(1); p.Write(Guid.Parse("77777777-1234-5678-9012-777777777777").ToByteArray()); p.Write(0x06000001); Text(p, "Fixture"); Text(p, "Fixture::Run"); });
        Frame(2, p => { p.Write(1); p.Write(12345UL); });
        Frame(3, p => { p.Write(2); Event(p, 0); Event(p, 1); });
        Frame(4, p => { p.Write(1); p.Write(2); p.Write(0); p.Write(1); });
        if (footer) Frame(5, p => { p.Write(0); p.Write(0); foreach (var n in new long[] { 1100, 0, 0, 0, 1, 1, 1, 0, 0 }) p.Write(n); });
        return stream.ToArray();
        void Frame(uint kind, Action<BinaryWriter> write)
        {
            using var payload = new MemoryStream(); using var writer = new BinaryWriter(payload, Encoding.UTF8, true); write(writer);
            var data = payload.ToArray(); Span<byte> prefix = stackalloc byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, kind); BinaryPrimitives.WriteUInt32LittleEndian(prefix[4..], (uint)data.Length);
            w.Write(prefix); w.Write(Crc32.Compute(data, Crc32.Compute(prefix))); w.Write(data);
        }
        static void Text(BinaryWriter writer, string text) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
        static void Event(BinaryWriter p, uint phase) { p.Write((1L << 48) | 1); p.Write(0L); p.Write(1010L); p.Write(phase == 0 ? 0L : 5L); p.Write(1); p.Write(1); p.Write(phase); p.Write(0); }
    }
    private static async Task<TraceView> Load(byte[] bytes, bool recover = false)
    {
        var store = new TraceStore(); using var stream = new MemoryStream(bytes);
        await TraceImport.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; }, recover);
        return store.GetView();
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task RealWireShapesRetainNativeProvenanceAcrossBinaryAndChrome(int version)
    {
        var trace = await Load(Recording(version)); var index = new NativeCaptureIndex(trace);
        Assert.Equal(version, index.Process!.FormatVersion); Assert.Equal(12345UL, Assert.Single(index.Threads).OsThreadId);
        Assert.Equal(1, index.PairedSpans); Assert.False(index.Incomplete); Assert.Equal("IL supplied", Assert.Single(index.Methods).Status);
        Assert.DoesNotContain(trace.Symbols.Values, s => s.StartsWith(NativeMetadataSymbol.Prefix));
        using var binary = new MemoryStream(); await TraceExport.WriteBinaryAsync(binary, trace);
        var copy = await Load(binary.ToArray()); Assert.Equal(trace.Events.OrderBy(e => e.Id), copy.Events.OrderBy(e => e.Id));
        Assert.Equal(638_000_000_000_000_000L, copy.UtcStartTicks);
        Assert.Equal(trace.NativeMetadata.OrderBy(p => p.Key), copy.NativeMetadata.OrderBy(p => p.Key));
        using var chrome = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(chrome, trace);
        using var document = JsonDocument.Parse(chrome.ToArray());
        Assert.Contains(document.RootElement.GetProperty("traceEvents").EnumerateArray(), e => e.GetProperty("ph").GetString() == "X" && e.GetProperty("args").GetProperty("nativeOutcome").GetString() == "Returned");
        Assert.Contains("OS thread 12345", Encoding.UTF8.GetString(chrome.ToArray()));
    }
    [Fact] public async Task EveryTruncationFailsStrictAndEveryV2ByteCorruptionFails()
    {
        var bytes = Recording();
        for (var n = 0; n < bytes.Length; n++) await Assert.ThrowsAsync<InvalidDataException>(() => Load(bytes[..n]));
        for (var n = 0; n < bytes.Length; n++) { var corrupt = bytes.ToArray(); corrupt[n] ^= 0x40; await Assert.ThrowsAsync<InvalidDataException>(() => Load(corrupt)); }
    }
    [Fact] public async Task TruncationRecoveryNeverFabricatesTerminalSuccess()
    {
        var trace = await Load(Recording(footer: false), true); var index = new NativeCaptureIndex(trace);
        Assert.True(index.Incomplete); Assert.False(index.End!.HasFooter); Assert.True(index.End.TruncatedTail); Assert.Equal(1, index.PairedSpans);
    }
    [Fact] public async Task FragmentedNonSeekableInputAndCancellationAreSupported()
    {
        using var input = new Fragmented(Recording()); var store = new TraceStore();
        await TraceImport.ReadAsync(input, b => { store.Add(b); return ValueTask.CompletedTask; }, false);
        Assert.Equal(1, new NativeCaptureIndex(store.GetView()).PairedSpans);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, cancellationToken: stop.Token));
    }
    [Fact] public async Task MissingPairDuplicateAndInvalidNestingRemainIncomplete()
    {
        var trace = await Load(Recording());
        Assert.True(new NativeCaptureIndex(trace with { Events = [trace.Events[0]] }).Incomplete);
        Assert.True(new NativeCaptureIndex(trace with { Events = [trace.Events[1]] }).Incomplete);
        Assert.Equal(1, new NativeCaptureIndex(trace with { Events = [trace.Events[0], trace.Events[0], trace.Events[1]] }).InvalidGroups);
        Assert.Equal(1, new NativeCaptureIndex(trace with { Events = [trace.Events[0], trace.Events[1] with { Timestamp = 1011 }] }).InvalidGroups);
    }
    [Fact] public void NativeMetadataIsValidatedBeforeAcceptingAndUpdatesBySequence()
    {
        var session = Guid.NewGuid(); var store = new TraceStore();
        TraceBlock Block(long sequence, int id, NativeMetadata value) => new() { SessionId = session, Sequence = sequence, Frequency = 1000, Symbols = [new(id, NativeMetadataSymbol.Encode(value))] };
        store.Add(Block(1, 10, new NativeProcessInfo(123, 8)));
        store.Add(Block(3, 12, new NativeMethodInfo(1, 2, 0, 4)));
        var old = store.GetView(); store.Add(Block(2, 11, new NativeMethodInfo(1, 1, 0, 0)));
        Assert.Equal(old.NativeMetadata, store.GetView().NativeMetadata);
        var invalid = Block(4, 13, new NativeMethodInfo(1, 2, 0, 5)) with { Symbols = [new(13, NativeMetadataSymbol.Prefix + "[9]")] };
        Assert.Throws<InvalidDataException>(() => store.Add(invalid));
        store.Add(Block(4, 13, new NativeMethodInfo(1, 2, 0, 5)));
        Assert.Equal(4, ((NativeMethodInfo)old.NativeMetadata[(3, 1)]).Compilations);
        Assert.Equal(5, ((NativeMethodInfo)store.GetView().NativeMetadata[(3, 1)]).Compilations);
        Assert.Throws<InvalidDataException>(() => store.Add(Block(5, 14, new NativeProcessInfo(124, 8))));
        Assert.Throws<InvalidDataException>(() => store.Add(Block(5, 14, new NativeThreadInfo(1, 42)) with { Events = [new(1, 0, 0, 0, 0, 0, 1, 14, Subsystem.Application)] }));
    }
    private sealed class Fragmented(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); var n = Math.Min(3, Math.Min(buffer.Length, bytes.Length - _position)); bytes.AsMemory(_position, n).CopyTo(buffer); _position += n; return ValueTask.FromResult(n); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }
}
