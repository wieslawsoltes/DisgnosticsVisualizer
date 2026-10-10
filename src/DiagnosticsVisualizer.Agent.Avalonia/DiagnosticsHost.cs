using System.Diagnostics;
using Avalonia;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>Target-side collector. All file/network work runs outside the application's UI thread.</summary>
public sealed class DiagnosticsHost : IAsyncDisposable
{
    private static DiagnosticsHost? _current;
    private readonly AgentOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _subscribersGate = new();
    private readonly List<LocalCaptureSubscription> _subscribers = [];
    private readonly FrameQueue _networkFrames = new(64, 8 * 1024 * 1024);
    private readonly FileStream? _archive;
    private readonly Task _pump, _network;
    private long _blocks, _networkDrops;
    private int _disposed;
    private string _connection = "Local capture";
    private string? _lastError;
    public static DiagnosticsHost? Current => Volatile.Read(ref _current);
    public CaptureSession Session { get; }
    public AvaloniaInspector Inspector { get; }
    public AgentStatus Status => new(Session.Recording, Interlocked.Read(ref _blocks), Interlocked.Read(ref _networkDrops),
        Volatile.Read(ref _connection), Volatile.Read(ref _lastError));
    private DiagnosticsHost(AgentOptions options)
    {
        options.Validate(); _options = options;
        if (options.TracePath is not null)
        {
            var path = Path.GetFullPath(options.TracePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _archive = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        try { Session = Probe.Start(options.Capture); }
        catch { _archive?.Dispose(); throw; }
        Inspector = new(Session, options); Inspector.InspectionError += OnInspectionError;
        _pump = Task.Run(PumpAsync);
        _network = options.Endpoint is null ? Task.CompletedTask : Task.Run(NetworkLoopAsync);
    }
    public static DiagnosticsHost Start(AgentOptions? options = null)
    {
        if (Current is not null) throw new InvalidOperationException("A diagnostics host is already running.");
        var host = new DiagnosticsHost(options ?? new());
        if (Interlocked.CompareExchange(ref _current, host, null) is not null)
        { _ = host.DisposeAsync(); throw new InvalidOperationException("A diagnostics host is already running."); }
        return host;
    }
    /// <summary>Called by the weaver's generated module initializer. Inert unless explicitly enabled.</summary>
    public static void InitializeFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("AVTRACE_ENABLED") != "1" || Current is not null) return;
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("AVTRACE_ENDPOINT");
            var path = Environment.GetEnvironmentVariable("AVTRACE_FILE");
            var host = Start(new()
            {
                TracePath = string.IsNullOrWhiteSpace(path) ? null : path,
                Endpoint = string.IsNullOrWhiteSpace(endpoint) ? null : new Uri(endpoint),
                Token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN"),
                RevealPrimitiveValues = Environment.GetEnvironmentVariable("AVTRACE_REVEAL_PRIMITIVES") == "1",
                RevealTextValues = Environment.GetEnvironmentVariable("AVTRACE_REVEAL_TEXT") == "1"
            });
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            };
        }
        catch (Exception error)
        {
            // Never include environment values, tokens, target data, or exception messages in bootstrap logs.
            Trace.WriteLine($"Diagnostics Visualizer bootstrap unavailable: {error.GetType().Name}");
        }
    }
    public void AttachRoot(Visual root) => Inspector.AttachRoot(root);
    public LocalCaptureSubscription Subscribe()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var subscriber = new LocalCaptureSubscription(RemoveSubscriber);
        lock (_subscribersGate) _subscribers.Add(subscriber);
        Inspector.RequestSnapshot(); return subscriber;
    }
    private void RemoveSubscriber(LocalCaptureSubscription subscription)
    { lock (_subscribersGate) _subscribers.Remove(subscription); }
    private void OnInspectionError(string error) => Volatile.Write(ref _lastError, error);
    public TraceCommand Execute(TraceCommand command)
    {
        switch (command.Kind)
        {
            case CommandKind.RequestSnapshot: Inspector.RequestSnapshot(command.ObjectId); break;
            case CommandKind.SelectObject: Inspector.RequestSnapshot(command.ObjectId); break;
            case CommandKind.Pause: Session.SetRecording(false); break;
            case CommandKind.Resume: Session.SetRecording(true); break;
            case CommandKind.Ping: break;
            default: return new(command.RequestId, CommandKind.Response, 0, "UNSUPPORTED_COMMAND");
        }
        return new(command.RequestId, CommandKind.Response, command.ObjectId, "OK");
    }
    private async Task PumpAsync()
    {
        var lastSnapshot = Stopwatch.GetTimestamp(); var lastFlush = lastSnapshot;
        try
        {
            using var timer = new PeriodicTimer(_options.FlushInterval);
            Inspector.RequestSnapshot();
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Stopwatch.GetElapsedTime(lastSnapshot) >= _options.SnapshotInterval)
                { Inspector.RequestSnapshot(); lastSnapshot = Stopwatch.GetTimestamp(); }
                await PublishAsync(Session.Drain(), _stop.Token).ConfigureAwait(false);
                if (_archive is not null && Stopwatch.GetElapsedTime(lastFlush) > TimeSpan.FromSeconds(1))
                { await _archive.FlushAsync(_stop.Token).ConfigureAwait(false); lastFlush = Stopwatch.GetTimestamp(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref _lastError, $"Capture pump stopped: {error.GetType().Name}"); }
        finally
        {
            // Closing a trace drains the bounded remainder. Crash recovery accepts only a truncated final frame.
            using var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                while (!drainTimeout.IsCancellationRequested)
                {
                    var block = Session.Drain();
                    if (block.Events.Length == 0 && block.Snapshot is null) break;
                    await PublishAsync(block, drainTimeout.Token).ConfigureAwait(false);
                }
                if (_archive is not null) await _archive.FlushAsync(drainTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) { Volatile.Write(ref _lastError, $"Final trace flush: {error.GetType().Name}"); }
            _networkFrames.Complete();
            lock (_subscribersGate) { foreach (var subscriber in _subscribers) subscriber.Complete(); _subscribers.Clear(); }
        }
    }
    private async Task PublishAsync(TraceBlock block, CancellationToken cancellationToken)
    {
        var frame = TraceCodec.Encode(block); Interlocked.Increment(ref _blocks);
        lock (_subscribersGate)
            for (var i = _subscribers.Count - 1; i >= 0; i--) if (!_subscribers[i].Publish(block)) _subscribers.RemoveAt(i);
        if (_archive is not null) await _archive.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        if (_options.Endpoint is not null && !_networkFrames.TryWrite(frame)) Interlocked.Increment(ref _networkDrops);
    }
    private async Task NetworkLoopAsync()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Volatile.Write(ref _connection, "Connecting");
                await using IFrameConnection connection = _options.Endpoint!.Scheme is "ws" or "wss"
                    ? await WebSocketFrameConnection.ConnectAsync(_options.Endpoint, _options.Token!, true, _stop.Token).ConfigureAwait(false)
                    : await StreamFrameConnection.ConnectAsync(_options.Endpoint, _options.Token!, true, _stop.Token).ConfigureAwait(false);
                Volatile.Write(ref _connection, "Connected"); delay = TimeSpan.FromMilliseconds(250);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var receive = ReceiveCommandsAsync(connection, linked.Token);
                var send = SendFramesAsync(connection, linked.Token);
                await Task.WhenAny(receive, send).ConfigureAwait(false); linked.Cancel();
                try { await Task.WhenAll(receive, send).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) { Volatile.Write(ref _lastError, $"Transport: {error.GetType().Name}"); }
            Volatile.Write(ref _connection, "Disconnected; retrying");
            try { await Task.Delay(delay, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromMilliseconds(Math.Min(10_000, delay.TotalMilliseconds * 2));
        }
        Volatile.Write(ref _connection, "Stopped");
    }
    private async Task SendFramesAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        await foreach (var frame in _networkFrames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }
    private async Task ReceiveCommandsAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var command = CommandCodec.Decode(frame); var response = Execute(command);
            await connection.SendAsync(CommandCodec.Encode(response), cancellationToken).ConfigureAwait(false);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.CompareExchange(ref _current, null, this);
        Session.SetRecording(false); Inspector.InspectionError -= OnInspectionError; Inspector.Dispose();
        _stop.Cancel();
        try { await Task.WhenAll(_pump, _network).ConfigureAwait(false); }
        finally { Session.Dispose(); if (_archive is not null) await _archive.DisposeAsync().ConfigureAwait(false); _stop.Dispose(); }
    }
}
