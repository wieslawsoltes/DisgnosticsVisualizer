using System.Buffers.Binary;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class NativeRevertTests
{
    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(-8, unchecked((int)0x80004005), true)]
    public async Task ReversionIsAnIndependentRuntimeOutcomeNotExecutionProof(int status, int hresult, bool incomplete)
    {
        var store = new TraceStore();
        using var input = new MemoryStream(WithStatus(3, status, hresult));
        await NativeTraceFile.ReadAsync(input, b => { store.Add(b); return ValueTask.CompletedTask; }, false);
        var trace = store.GetView(); var index = new NativeCaptureIndex(trace);
        Assert.Equal(3, index.Process!.FormatVersion);
        Assert.Equal(1, index.PairedSpans); Assert.Equal(incomplete, index.Incomplete);
        Assert.Equal(status == 3 ? "Reverted for future calls" : "Revert failed", Assert.Single(index.Methods).Status);
        using var binary = new MemoryStream(); await TraceExport.WriteBinaryAsync(binary, trace); binary.Position = 0;
        var copy = new TraceStore(); await TraceImport.ReadAsync(binary, b => { copy.Add(b); return ValueTask.CompletedTask; }, false);
        Assert.Equal(trace.NativeMetadata, copy.GetView().NativeMetadata);
        Assert.Equal(trace.Events, copy.GetView().Events);
        var metadataOnly = new NativeCaptureIndex(trace with { Events = [] });
        Assert.Equal(0, metadataOnly.PairedSpans); Assert.True(metadataOnly.Incomplete);
    }

    [Theory]
    [InlineData(2, 3, 0)]
    [InlineData(2, -8, unchecked((int)0x80004005))]
    [InlineData(3, 3, unchecked((int)0x80004005))]
    [InlineData(3, -8, 0)]
    [InlineData(3, 4, 0)]
    [InlineData(3, -9, 0)]
    public async Task ReversionStatusesRequireVersionAndConsistentResults(int version, int status, int hresult)
    {
        using var input = new MemoryStream(WithStatus(version, status, hresult));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
    }

    [Fact]
    public async Task V3HeaderAndEveryReversionFrameByteRemainChecksummed()
    {
        var bytes = WithStatus(3, 3, 0);
        for (var i = 0; i < bytes.Length; ++i)
        {
            var corrupt = (byte[])bytes.Clone(); corrupt[i] ^= 0x20;
            using var input = new MemoryStream(corrupt);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeTraceFile.ReadAsync(input, _ => ValueTask.CompletedTask, false));
        }
    }

    private static byte[] WithStatus(int version, int status, int hresult)
    {
        var bytes = NativeCaptureCompatibilityTests.Recording(version);
        for (var offset = 68; offset < bytes.Length;)
        {
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            if (kind == 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 16), status);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset + 20), hresult);
                var crc = Crc32.Compute(bytes.AsSpan(offset + 12, length), Crc32.Compute(bytes.AsSpan(offset, 8)));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), crc);
                return bytes;
            }
            offset += 12 + length;
        }
        throw new InvalidDataException("Reference fixture has no status frame.");
    }
}
