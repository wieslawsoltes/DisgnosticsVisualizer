using System.Collections.Concurrent;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Collector;

/// <summary>Single target session, multiple authenticated viewers. All queues and retained history are bounded.</summary>
public sealed class CaptureHub(CollectorOptions options, ILogger<CaptureHub> logger) : IAsyncDisposable
{
    private sealed class Viewer(IFrameConnection connection)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly IFrameConnection Connection = connection;
        public readonly FrameQueue Queue = new(64, 16 * 1024 * 1024);
        public readonly CancellationTokenSource Stop = new();
        public long Window = Environment.TickCount64;
        public int Commands;
    }
    private sealed record Pending(Viewer Viewer, long OriginalId, long Created);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _ingest = new(1, 1);
    private readonly List<Viewer> _viewers = [];
    private readonly Queue<byte[]> _history = new();
    private readonly ConcurrentDictionary<long, Pending> _requests = new();
    private readonly Dictionary<long, string> _objectNames = [];
    private IFrameConnection? _producer;
    private Guid _session;
    private long _historyBytes, _requestId, _frames, _archiveBytes;
    private byte[]? _checkpoint;
    private FileStream? _archive;
    private SecureDatagramAssembler? _datagrams;
    private long _udpRejected, _udpExpired;
    public long Frames => Interlocked.Read(ref _frames);
    public bool Connected { get { lock (_gate) return _producer is not null; } }
    public int Viewers { get { lock (_gate) return _viewers.Count; } }
    public string? ArchiveError { get; private set; }
    public async Task RunProducerAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_producer is not null) throw new InvalidOperationException("A target is already connected to this collector.");
            _producer = connection;
        }
        try
        {
            while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                if (WireEnvelope.GetKind(frame) == FrameKind.Trace) await PublishAsync(frame, false, cancellationToken).ConfigureAwait(false);
                else
                {
                    var response = CommandCodec.Decode(frame);
                    if (response.Kind != CommandKind.Response) throw new InvalidDataException("Target sent an unexpected command.");
                    if (_requests.TryRemove(response.RequestId, out var pending))
                        Enqueue(pending.Viewer, CommandCodec.Encode(response with { RequestId = pending.OriginalId }));
                }
            }
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_producer, connection)) _producer = null;
            _requests.Clear();
            Broadcast(CommandCodec.Encode(new(0, CommandKind.Response, 0, "SOURCE_DISCONNECTED")));
        }
    }
    public async Task RunViewerAsync(IFrameConnection connection, CancellationToken cancellationToken)
    {
        var viewer = new Viewer(connection);
        lock (_gate)
        {
            if (_viewers.Count >= options.MaxViewers) throw new InvalidOperationException("Collector viewer limit reached.");
            _viewers.Add(viewer);
            if (_checkpoint is not null) Enqueue(viewer, _checkpoint);
            // A fresh viewer receives a bounded recent tail; explicit sequence gaps remain observable.
            long copied = 0;
            var tail = _history.Reverse().TakeWhile(frame => (copied += frame.Length) <= 8 * 1024 * 1024).Take(48).Reverse().ToArray();
            foreach (var frame in tail) if (!ReferenceEquals(frame, _checkpoint)) Enqueue(viewer, frame);
            if (_producer is null) Enqueue(viewer, CommandCodec.Encode(new(0, CommandKind.Response, 0, "NO_SOURCE")));
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, viewer.Stop.Token);
        var send = SendViewerAsync(viewer, linked.Token);
        var receive = ReceiveViewerAsync(viewer, linked.Token);
        try { await Task.WhenAny(send, receive).ConfigureAwait(false); linked.Cancel(); await Task.WhenAll(send, receive).ConfigureAwait(false); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            lock (_gate) _viewers.Remove(viewer);
            foreach (var request in _requests.Where(pair => pair.Value.Viewer == viewer).Select(pair => pair.Key)) _requests.TryRemove(request, out _);
            viewer.Queue.Complete(); viewer.Stop.Dispose();
        }
    }
    private static async Task SendViewerAsync(Viewer viewer, CancellationToken cancellationToken)
    {
        await foreach (var frame in viewer.Queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await viewer.Connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }
    private async Task ReceiveViewerAsync(Viewer viewer, CancellationToken cancellationToken)
    {
        while (await viewer.Connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var command = CommandCodec.Decode(frame);
            if (command.Kind is not (CommandKind.RequestSnapshot or CommandKind.SelectObject or CommandKind.Pause or CommandKind.Resume or CommandKind.Ping))
                throw new InvalidDataException("Viewer command is not permitted.");
            var now = Environment.TickCount64;
            if (now - viewer.Window >= 1000) { viewer.Window = now; viewer.Commands = 0; }
            if (++viewer.Commands > 32) throw new InvalidDataException("Viewer exceeded the control request rate limit.");
            if (command.Argument.Length != 0) throw new InvalidDataException("This control command does not accept string arguments.");
            IFrameConnection? producer; lock (_gate) producer = _producer;
            if (producer is null) { Enqueue(viewer, CommandCodec.Encode(new(command.RequestId, CommandKind.Response, 0, "NO_SOURCE"))); continue; }
            foreach (var expired in _requests.Where(pair => now - pair.Value.Created > 30_000).Select(pair => pair.Key)) _requests.TryRemove(expired, out _);
            if (_requests.Count >= 1024) throw new InvalidDataException("Too many pending control requests.");
            var requestId = Interlocked.Increment(ref _requestId); _requests[requestId] = new(viewer, command.RequestId, now);
            try { await producer.SendAsync(CommandCodec.Encode(command with { RequestId = requestId }), cancellationToken).ConfigureAwait(false); }
            catch { _requests.TryRemove(requestId, out _); throw; }
        }
    }
    public async Task AcceptDatagramAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        byte[]? frame = null;
        lock (_gate)
        {
            if (_producer is null || _datagrams is null) return;
            _datagrams.TryAccept(packet.Span, out frame);
            _udpRejected = _datagrams.RejectedPackets; _udpExpired = _datagrams.ExpiredFrames;
        }
        if (frame is not null) await PublishAsync(frame, true, cancellationToken).ConfigureAwait(false);
    }
    private async Task PublishAsync(byte[] frame, bool datagram, CancellationToken cancellationToken)
    {
        var block = TraceCodec.Decode(frame);
        if (block.SessionId == Guid.Empty) throw new InvalidDataException("A nonempty trace session is required.");
        await _ingest.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var newSession = false;
            lock (_gate)
            {
                if (datagram && _session != block.SessionId) return;
                if (_session != block.SessionId)
                {
                    newSession = true;
                    if (_session != Guid.Empty)
                        foreach (var viewer in _viewers) { viewer.Queue.Complete(new IOException("Source session changed. Reconnect to start a new recording.")); viewer.Stop.Cancel(); }
                    _session = block.SessionId; _history.Clear(); _historyBytes = 0; _checkpoint = null;
                    _datagrams?.Dispose(); _datagrams = new(_session, NetworkSecurity.DeriveDatagramKey(options.Token));
                }
                if (block.Snapshot is not null) _checkpoint = frame;
                _history.Enqueue(frame); _historyBytes += frame.Length;
                while (_historyBytes > 32 * 1024 * 1024 || _history.Count > 2048) _historyBytes -= _history.Dequeue().Length;
            }
            if (newSession && options.ArchiveDirectory is not null)
            {
                if (_archive is not null) await _archive.DisposeAsync().ConfigureAwait(false);
                Directory.CreateDirectory(options.ArchiveDirectory); _archiveBytes = 0; ArchiveError = null;
                var path = Path.Combine(options.ArchiveDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{block.SessionId:N}.avtrace");
                _archive = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 256 * 1024, FileOptions.Asynchronous);
            }
            Interlocked.Increment(ref _frames); Broadcast(frame);
            if (_archive is not null && ArchiveError is null)
            {
                if (_archiveBytes + frame.Length > options.MaxArchiveBytes) ArchiveError = "Archive size limit reached; live forwarding continues.";
                else
                {
                    try { await _archive.WriteAsync(frame, cancellationToken).ConfigureAwait(false); _archiveBytes += frame.Length; }
                    catch (IOException) { ArchiveError = "Archive I/O failed; live forwarding continues."; }
                }
                if (ArchiveError is not null) logger.LogWarning("{ArchiveStatus}", ArchiveError);
            }
        }
        finally { _ingest.Release(); }
    }
    private void Broadcast(byte[] frame)
    { lock (_gate) foreach (var viewer in _viewers) Enqueue(viewer, frame); }
    private static void Enqueue(Viewer viewer, byte[] frame)
    {
        if (viewer.Queue.TryWrite(frame)) return;
        viewer.Queue.Complete(new IOException("Viewer exceeded the bounded transport buffer. Reconnect for a new checkpoint."));
        viewer.Stop.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { foreach (var viewer in _viewers) viewer.Stop.Cancel(); _datagrams?.Dispose(); }
        if (_archive is not null) await _archive.DisposeAsync().ConfigureAwait(false);
    }
}
