using System.Threading.Tasks.Sources;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

public sealed class AsyncSubject
{
    public int Touches;
    public async Task<int> TaskAsync(Task gate, int value)
    { await gate.ConfigureAwait(false); Touches++; return value; }
    public async ValueTask<int> ValueAsync(Task gate, int value)
    { await gate.ConfigureAwait(false); Touches++; return value; }
    public async Task FailAsync(Task gate, Exception error)
    { await gate.ConfigureAwait(false); throw error; }
    public async ValueTask EmptyValueAsync(Task gate) { await gate.ConfigureAwait(false); Touches++; }
    public async void VoidAsync(Task gate, Action done) { await gate.ConfigureAwait(false); Touches++; done(); }
    public async ValueTask<int> SingleUseAsync(SingleUseSource source) => await source.Create().ConfigureAwait(false);
}

public sealed class GenericAsyncSubject<T>
{
    public int Touches;
    public async Task<T> EchoAsync<TArgument>(Task gate, T value, TArgument argument)
    { await gate.ConfigureAwait(false); Touches++; GC.KeepAlive(argument); return value; }
}

public sealed class SingleUseSource : IValueTaskSource<int>
{
    private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };
    public int Consumptions;
    public ValueTask<int> Create() => new(this, _core.Version);
    public void Complete(int value) => _core.SetResult(value);
    public int GetResult(short token)
    {
        if (Interlocked.Increment(ref Consumptions) != 1) throw new InvalidOperationException("ValueTask was consumed twice");
        return _core.GetResult(token);
    }
    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}
