using System.Threading.Channels;

namespace DiagnosticsVisualizer.Transport;

/// <summary>Immutable after enqueue. Reliability is transport metadata, not part of the trace wire format.</summary>
public readonly record struct QueuedFrame(byte[] Bytes, bool Reliable);

/// <summary>Bounded by both messages and bytes. Exactly one reader; overflow is reported to the caller.</summary>
public sealed class FrameQueue
{
    private readonly Channel<QueuedFrame> _channel;
    private readonly long _maxBytes;
    private long _bytes;
    public FrameQueue(int maxFrames = 64, long maxBytes = 8 * 1024 * 1024)
    {
        if (maxFrames < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        _maxBytes = maxBytes;
        _channel = Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(maxFrames)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    }
    public bool TryWrite(byte[] frame, bool reliable = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Interlocked.Add(ref _bytes, frame.Length) > _maxBytes)
        { Interlocked.Add(ref _bytes, -frame.Length); return false; }
        if (_channel.Writer.TryWrite(new(frame, reliable))) return true;
        Interlocked.Add(ref _bytes, -frame.Length); return false;
    }
    public async IAsyncEnumerable<QueuedFrame> ReadPacketsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        { Interlocked.Add(ref _bytes, -frame.Bytes.Length); yield return frame; }
    }
    public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in ReadPacketsAsync(cancellationToken).ConfigureAwait(false)) yield return frame.Bytes;
    }
    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
