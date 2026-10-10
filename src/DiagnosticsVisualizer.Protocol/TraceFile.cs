using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

public sealed record TraceReadResult(long Blocks, long Events, bool TruncatedTail, long DeltaFrames = 0);

public static class TraceFile
{
    public static async ValueTask WriteAsync(Stream stream, TraceBlock block, CancellationToken cancellationToken = default)
        => await stream.WriteAsync(TraceCodec.Encode(block), cancellationToken).ConfigureAwait(false);

    public static async Task<TraceReadResult> ReadAsync(Stream stream, Func<TraceBlock, ValueTask> accept,
        bool recoverIncompleteTail = true, CancellationToken cancellationToken = default)
    {
        long blocks = 0, events = 0; var decoder = new TraceStreamDecoder();
        var header = new byte[WireEnvelope.HeaderBytes];
        while (true)
        {
            var headerRead = await ReadPartialAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (headerRead == 0) return new(blocks, events, false, decoder.DeltaFrames);
            if (headerRead != header.Length)
            {
                if (recoverIncompleteTail) return new(blocks, events, true, decoder.DeltaFrames);
                throw new InvalidDataException("Truncated trace header.");
            }
            var length = WireEnvelope.ReadLength(header);
            var frame = new byte[length]; header.CopyTo(frame, 0);
            var read = await ReadPartialAsync(stream, frame.AsMemory(header.Length), cancellationToken).ConfigureAwait(false);
            if (read != length - header.Length)
            {
                if (recoverIncompleteTail) return new(blocks, events, true, decoder.DeltaFrames);
                throw new InvalidDataException("Truncated trace body.");
            }
            // Corruption of a complete frame is not silently treated as truncation.
            var block = decoder.Decode(frame);
            await accept(block).ConfigureAwait(false); blocks++; events += block.Events.Length;
        }
    }
    private static async ValueTask<int> ReadPartialAsync(Stream stream, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
