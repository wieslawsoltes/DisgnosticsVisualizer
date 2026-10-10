using System.Threading.Channels;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>An in-process viewer receives immutable batches; a slow viewer is disconnected, never blocks the target.</summary>
public sealed class LocalCaptureSubscription : IAsyncDisposable
{
    private readonly Channel<TraceBlock> _channel = Channel.CreateBounded<TraceBlock>(new BoundedChannelOptions(32)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly Action<LocalCaptureSubscription> _remove;
    private int _closed;
    internal LocalCaptureSubscription(Action<LocalCaptureSubscription> remove) => _remove = remove;
    internal bool Publish(TraceBlock block)
    {
        if (Volatile.Read(ref _closed) != 0) return false;
        if (_channel.Writer.TryWrite(block)) return true;
        Complete(new IOException("In-process viewer fell behind the bounded capture queue. Reconnect and request a checkpoint.")); return false;
    }
    internal void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _channel.Writer.TryComplete(error);
    }
    public IAsyncEnumerable<TraceBlock> ReadAllAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() { Complete(); _remove(this); return ValueTask.CompletedTask; }
}
