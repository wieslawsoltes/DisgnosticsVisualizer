using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Protocol;

public static class CommandCodec
{
    public static byte[] Encode(TraceCommand command)
    {
        if (!Enum.IsDefined(command.Kind) || command.Argument.Length > TraceLimits.MaxCommandBytes)
            throw new InvalidDataException("Invalid command.");
        var w = new WireWriter(); w.I64(command.RequestId); w.Byte((byte)command.Kind);
        w.I64(command.ObjectId); w.Text(command.Argument);
        return WireEnvelope.Seal(FrameKind.Command, w.Written);
    }
    public static TraceCommand Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > TraceLimits.MaxCommandBytes + 128) throw new InvalidDataException("Command too large.");
        var r = new WireReader(WireEnvelope.Open(frame, FrameKind.Command));
        var id = r.I64(); var kind = (CommandKind)r.Byte();
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown command.");
        var command = new TraceCommand(id, kind, r.I64(), r.Text()); r.Finish(); return command;
    }
}

public sealed record TraceReadResult(long Blocks, long Events, bool TruncatedTail, long DeltaFrames = 0);

public static class TraceFile
{
    /// <summary>Recovers only an incomplete final frame. Corruption is never silently ignored.</summary>
    public static async Task<TraceReadResult> ReadAsync(Stream stream,
        Func<TraceBlock, ValueTask> consume, bool recoverTruncatedTail = true,
        CancellationToken cancellationToken = default)
    {
        long blocks = 0, events = 0; var decoder = new TraceStreamDecoder();
        while (true)
        {
            byte[]? frame;
            try { frame = await WireEnvelope.ReadAsync(stream, cancellationToken).ConfigureAwait(false); }
            catch (EndOfStreamException) when (recoverTruncatedTail) { return new(blocks, events, true, decoder.DeltaFrames); }
            if (frame is null) return new(blocks, events, false, decoder.DeltaFrames);
            var block = decoder.Decode(frame);
            await consume(block).ConfigureAwait(false);
            blocks++; events += block.Events.Length;
        }
    }
    public static async ValueTask WriteAsync(Stream stream, TraceBlock block, CancellationToken cancellationToken = default)
        => await stream.WriteAsync(TraceCodec.Encode(block), cancellationToken).ConfigureAwait(false);
}
