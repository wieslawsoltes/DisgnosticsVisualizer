using System.Threading.Channels;

namespace DiagnosticsVisualizer.Transport;

/// <summary>Bounded by both messages and bytes; callers must surface overflow rather than silently drop.</summary>
public sealed class FrameQueue(int maxFrames = 64, long maxBytes = 8 * 1024 * 1024)
{
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maxFrames)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private long _bytes;
    public bool TryWrite(byte[] frame)
    {
        if (Interlocked.Add(ref _bytes, frame.Length) > maxBytes)
        { Interlocked.Add(ref _bytes, -frame.Length); return false; }
        if (_channel.Writer.TryWrite(frame)) return true;
        Interlocked.Add(ref _bytes, -frame.Length); return false;
    }
    public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        { Interlocked.Add(ref _bytes, -frame.Length); yield return frame; }
    }
    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
}
