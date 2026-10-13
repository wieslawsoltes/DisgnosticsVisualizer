using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Workbench;

public sealed record EventRow(TraceEvent Event, string Name, double StartMs, double DurationMs)
{
    public long Id => Event.Id;
    public Subsystem Subsystem => Event.Subsystem;
    public int ThreadId => Event.ThreadId;
    public long ObjectId => Event.ObjectId;
}

public sealed partial class WorkbenchModel : INotifyPropertyChanged, IAsyncDisposable
{
    private TraceStore _store = new(500_000);
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _connectionStop;
    private IFrameConnection? _connection;
    private Func<TraceCommand, Task<TraceCommand>>? _localCommand;
    private Task _receiveTask = Task.CompletedTask;
    private int _dirty, _refreshing, _generation, _disposed;
    private long _request, _selectedObject, _selectedEvent;
    private string _source = "No session", _status = "Ready", _error = "";
    private bool _recording, _isDemo;
    private Dictionary<long, TraceEvent> _events = [];
    public TraceView? Trace { get; private set; }
    public AnalysisResult? Analysis { get; private set; }
    public CostComparison[] Comparison { get; private set; } = [];
    public string Source { get => _source; private set => Set(ref _source, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public bool Recording { get => _recording; private set => Set(ref _recording, value); }
    public bool IsDemo { get => _isDemo; private set => Set(ref _isDemo, value); }
    private bool _nativeControlAvailable;
    private long _nativeCapabilityRequest;
    private Guid _nativeCapabilitySession;
    public bool SupportsRecordingControl => Trace?.NativeMetadata.Count is not > 0 ||
        Connected && _nativeControlAvailable && Analysis?.NativeCapture?.End is null;
    public bool Connected => _connection is not null || _localCommand is not null;
    public long SelectedObjectId => _selectedObject;
    public long SelectedEventId => _selectedEvent;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<TraceView, AnalysisResult>? TraceUpdated;
    public event Action<TraceEvent?, long, bool>? SelectionChanged;
    public event Action? ComparisonUpdated;
    public WorkbenchModel(bool loadDemo = true)
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync(); _refreshTimer.Start();
        if (loadDemo) LoadDemo();
    }
    public void LoadDemo()
    {
        if (Connected) { ReportError("Disconnect before replacing a live session with the example."); return; }
        ResetReview(); Interlocked.Increment(ref _generation); _store = new(500_000);
        foreach (var block in DemoTraceFactory.Create()) _store.Add(block);
        IsDemo = true; Source = "DEMO · synthetic Dashboard interaction"; Status = "Offline example · no application is being monitored"; Error = "";
        Apply(_store.GetView(), TraceAnalyzer.Analyze(_store.GetView())); SelectObject(5, false);
        var candidate = Trace?.Events.FirstOrDefault(e => e.Subsystem == Subsystem.Properties && e.ObjectId == 5);
        if (candidate is { Id: not 0 } selected) SelectEvent(selected.Id, false);
    }
    public void LoadNativeDemo()
    {
        if (Connected) { ReportError("Disconnect before replacing a live session with an example."); return; }
        ResetReview(); Interlocked.Increment(ref _generation); _store = new(500_000);
        foreach (var block in DemoTraceFactory.CreateNative()) _store.Add(block);
        IsDemo = true; Source = "DEMO · synthetic native method recording"; Status = "Offline native example · no process is attached"; Error = "";
        var trace = _store.GetView(); Apply(trace, TraceAnalyzer.Analyze(trace));
        SelectEvent(trace.Events.First(e => e.Kind == TraceEventKind.Span).Id, false);
    }
    public async Task ConnectAsync(Uri endpoint, string token)
    {
        await DisconnectAsync(); Error = ""; Status = "Authenticating with collector…";
        try
        {
            var connection = endpoint.Scheme is "ws" or "wss"
                ? (IFrameConnection)await WebSocketFrameConnection.ConnectAsync(endpoint, token)
                : await StreamFrameConnection.ConnectAsync(endpoint, token, false);
            ResetReview(); _store = new(500_000); _connection = connection; _connectionStop = new();
            var generation = Interlocked.Increment(ref _generation); var store = _store;
            IsDemo = false; Source = $"LIVE · {endpoint.Host}:{endpoint.Port}"; Status = "Connected · awaiting target batches";
            Raise(nameof(Connected)); _receiveTask = ReceiveAsync(connection, store, generation, _connectionStop.Token);
            await SendCommandAsync(CommandKind.RequestSnapshot);
        }
        catch (Exception error) { Status = "Connection failed"; ReportError($"{error.GetType().Name}: {error.Message}"); throw; }
    }
    public async Task AttachInProcessAsync(IAsyncEnumerable<TraceBlock> stream, Func<TraceCommand, Task<TraceCommand>> execute)
    {
        await DisconnectAsync(); ResetReview(); _store = new(500_000); _connectionStop = new(); _localCommand = execute;
        var store = _store; var generation = Interlocked.Increment(ref _generation);
        IsDemo = false; Source = "LIVE · in-process target"; Status = "In-process viewing contributes to target overhead"; Error = ""; Raise(nameof(Connected));
        _receiveTask = ReceiveLocalAsync(stream, store, generation, _connectionStop.Token);
        await SendCommandAsync(CommandKind.RequestSnapshot);
    }
    private async Task ReceiveLocalAsync(IAsyncEnumerable<TraceBlock> stream, TraceStore store, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var block in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            { store.Add(block); Interlocked.Exchange(ref _dirty, 1); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { PostError(generation, $"Local capture: {error.GetType().Name}: {error.Message}"); }
    }
    private async Task ReceiveAsync(IFrameConnection connection, TraceStore store, int generation, CancellationToken cancellationToken)
    {
        var decoder = new TraceStreamDecoder();
        try
        {
            while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                if (WireEnvelope.GetKind(frame) is FrameKind.Trace or FrameKind.SnapshotDelta)
                {
                    var block = decoder.Decode(frame); store.Add(block);
                    if (block.Events.Length > 0 || block.Symbols.Length > 0 || block.Snapshot is not null || block.Recording != Recording || block.DroppedEvents != Trace?.DroppedEvents)
                        Interlocked.Exchange(ref _dirty, 1);
                }
                else
                {
                    var response = CommandCodec.Decode(frame);
                    if (response.RequestId == Interlocked.Read(ref _nativeCapabilityRequest) && _nativeCapabilityRequest != 0)
                    {
                        Dispatcher.UIThread.Post(() => { if (generation == _generation && response.RequestId == Interlocked.Read(ref _nativeCapabilityRequest)) { _nativeControlAvailable = response.Argument == "NATIVE_PAUSE_RESUME"; Raise(nameof(SupportsRecordingControl)); } });
                        continue;
                    }
                    if (response.Argument == "SOURCE_DISCONNECTED") Dispatcher.UIThread.Post(() => { if (generation == _generation) { _nativeControlAvailable = false; Raise(nameof(SupportsRecordingControl)); } });
                    if (response.Argument is not ("OK" or "AUTHENTICATED")) Dispatcher.UIThread.Post(() =>
                    { if (generation == _generation) Status = response.Argument switch
                        { "NO_SOURCE" => "Collector connected · waiting for an instrumented application", "SOURCE_DISCONNECTED" => "Target disconnected · retained trace remains available", _ => response.Argument }; });
                }
            }
            if (!cancellationToken.IsCancellationRequested) PostError(generation, "Collector connection closed. The retained trace is still available; reconnect to resume.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { PostError(generation, $"Capture transport: {error.GetType().Name}: {error.Message}"); }
    }
    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _generation); _connectionStop?.Cancel();
        _nativeControlAvailable = false; _nativeCapabilitySession = Guid.Empty; Interlocked.Exchange(ref _nativeCapabilityRequest, 0);
        if (_connection is not null) await _connection.DisposeAsync();
        try { await _receiveTask; } catch (OperationCanceledException) { }
        _connection = null; _localCommand = null; _connectionStop?.Dispose(); _connectionStop = null; _receiveTask = Task.CompletedTask;
        Recording = false; Raise(nameof(Connected)); Status = "Disconnected · retained history available";
    }
    public async Task ImportAsync(Stream stream, string name, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(); ResetReview(); Status = "Reading and validating trace…"; Error = "";
        var store = new TraceStore(500_000); var generation = Interlocked.Increment(ref _generation);
        var result = await TraceImport.ReadAsync(stream, block => { store.Add(block); return ValueTask.CompletedTask; }, true, cancellationToken);
        var prepared = await Task.Run(() => { var view = store.GetView(); return Prepare(view); }, cancellationToken);
        if (generation != _generation) return;
        _store = store; IsDemo = false; Source = $"FILE · {name}"; Apply(prepared.view, prepared.analysis, prepared.index, prepared.profile, prepared.selected, prepared.range);
        Status = result.TruncatedTail ? "Recovered complete batches; the final incomplete batch was discarded" : $"Imported {result.Events:N0} events in {result.Blocks:N0} batches";
        if (prepared.view.EvictedEvents > 0) Status += $" · retained last {prepared.view.Events.Length:N0} events";
        SelectObject(prepared.view.Snapshot.SelectedObject, false);
    }
    public async Task CompareAsync(Stream baseline, CancellationToken cancellationToken = default)
    {
        if (Analysis is null) throw new InvalidOperationException("Open a current trace first.");
        var store = new TraceStore(500_000); await TraceImport.ReadAsync(baseline, block => { store.Add(block); return ValueTask.CompletedTask; }, true, cancellationToken);
        var before = await Task.Run(() => TraceAnalyzer.Analyze(store.GetView()), cancellationToken);
        Comparison = TraceComparison.Compare(before, Analysis); ComparisonUpdated?.Invoke(); Status = "Baseline comparison uses recorded self time; normalize workload before interpreting changes.";
    }
    public Task ExportAsync(Stream stream, bool chrome, CancellationToken cancellationToken = default)
    {
        var trace = Trace ?? throw new InvalidOperationException("There is no trace to export.");
        return chrome ? TraceExport.WriteChromeJsonAsync(stream, trace, cancellationToken) : TraceExport.WriteBinaryAsync(stream, trace, cancellationToken);
    }
    public async Task ToggleRecordingAsync()
    {
        if (!Connected) { ReportError("Connect to an instrumented target to start or pause recording."); return; }
        if (!SupportsRecordingControl) { ReportError("Native pause/resume requires an explicitly configured live sidecar and an acknowledged control capability."); return; }
        await SendCommandAsync(Recording ? CommandKind.Pause : CommandKind.Resume);
    }
    public Task RequestSnapshotAsync() => SendCommandAsync(CommandKind.RequestSnapshot, _selectedObject);
    private async Task SendCommandAsync(CommandKind kind, long objectId = 0)
    {
        var command = new TraceCommand(Interlocked.Increment(ref _request), kind, objectId);
        if (_connection is not null) await _connection.SendAsync(CommandCodec.Encode(command), _connectionStop?.Token ?? default);
        else if (_localCommand is not null) await _localCommand(command);
    }
    private async Task RequestObjectAsync(long objectId)
    {
        try { await SendCommandAsync(CommandKind.SelectObject, objectId); }
        catch (Exception error) { ReportError($"Selection request failed: {error.GetType().Name}"); }
    }
    public void SelectObject(long objectId, bool requestTarget = true)
    {
        _selectedObject = objectId;
        var selected = _events.GetValueOrDefault(_selectedEvent);
        if (selected.ObjectId != objectId) { _selectedEvent = 0; selected = default; }
        RecordNavigation();
        SelectionChanged?.Invoke(selected.Id == 0 ? null : selected, objectId, false);
        if (requestTarget && Connected && objectId != 0) _ = RequestObjectAsync(objectId);
    }
    public void SelectEvent(long eventId, bool focus)
    {
        if (!_events.TryGetValue(eventId, out var selected)) return;
        _selectedEvent = eventId;
        if (selected.ObjectId != 0) _selectedObject = selected.ObjectId;
        RecordNavigation();
        SelectionChanged?.Invoke(selected, _selectedObject, focus);
        if (Connected && selected.ObjectId != 0) _ = RequestObjectAsync(selected.ObjectId);
    }
    public EventRow[] FilterEvents(string? filter, Subsystem? subsystem = null, bool selectedObjectOnly = false)
    {
        if (Trace is not { } trace) return [];
        try
        {
            var predicate = TraceQuery.Compile(filter, trace); QueryError = "";
            return trace.Events.Where(e => (Range is null || Range.Value.Intersects(e)) &&
                (subsystem is null || e.Subsystem == subsystem) && (!selectedObjectOnly || e.ObjectId == _selectedObject) && predicate(e))
                .TakeLast(10_000).Select(e => new EventRow(e, trace.Name(e), trace.Milliseconds(e.Timestamp - trace.OriginTimestamp), trace.Milliseconds(e.Duration))).ToArray();
        }
        catch (TraceQueryException error) { QueryError = error.Message; return []; }
    }
    public async Task RefreshAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _dirty, 0) == 0) return;
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) { Interlocked.Exchange(ref _dirty, 1); return; }
        var store = _store; var generation = _generation; var range = Range;
        try
        {
            var prepared = await Task.Run(() => { var view = store.GetView(); return Prepare(view, range); });
            if (generation == _generation) Apply(prepared.view, prepared.analysis, prepared.index, prepared.profile, prepared.selected, prepared.range);
        }
        catch (Exception error) { ReportError($"Analysis failed: {error.GetType().Name}: {error.Message}"); }
        finally { Volatile.Write(ref _refreshing, 0); }
    }
    private void Apply(TraceView trace, AnalysisResult analysis, TraceProfile? index = null, ProfileResult? profile = null, ProfileResult? selectedProfile = null, TraceRange? analyzedRange = null)
    {
        Trace = trace; Analysis = analysis;
        if (Connected && trace.NativeMetadata.Count > 0 && _nativeCapabilitySession != trace.SessionId)
        { _nativeCapabilitySession = trace.SessionId; _nativeControlAvailable = false; _ = QueryNativeCapabilitiesAsync(_generation); }
        Raise(nameof(SupportsRecordingControl)); _events = trace.Events.ToDictionary(e => e.Id); Recording = trace.Recording;
        _profileIndex = index ?? new TraceProfile(trace); FullProfile = profile ?? _profileIndex.Analyze();
        Profile = Range is null ? FullProfile : analyzedRange == Range ? selectedProfile : null;
        if (Range is { } range && Profile is null) _ = RefreshRangeAsync(range);
        TraceUpdated?.Invoke(trace, analysis); ProfileUpdated?.Invoke();
        if (_selectedObject == 0) _selectedObject = trace.Snapshot.SelectedObject;
        SelectionChanged?.Invoke(_events.TryGetValue(_selectedEvent, out var selected) ? selected : null, _selectedObject, false);
        NavigationChanged(); Raise(nameof(Trace)); Raise(nameof(Analysis));
    }
    private async Task QueryNativeCapabilitiesAsync(int generation)
    {
        var id = Interlocked.Increment(ref _request); Interlocked.Exchange(ref _nativeCapabilityRequest, id);
        var command = new TraceCommand(id, CommandKind.Capabilities);
        try
        {
            if (_connection is not null) await _connection.SendAsync(CommandCodec.Encode(command), _connectionStop?.Token ?? default);
            else if (_localCommand is not null)
            {
                var reply = await _localCommand(command);
                if (generation == _generation) { _nativeControlAvailable = reply.Argument == "NATIVE_PAUSE_RESUME"; Raise(nameof(SupportsRecordingControl)); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        { if (generation == _generation) { _nativeControlAvailable = false; Raise(nameof(SupportsRecordingControl)); } }
    }
    public void ReportError(string error) { Error = error; Status = "Attention required"; }
    public void ClearError() => Error = "";
    private void PostError(int generation, string error) => Dispatcher.UIThread.Post(() => { if (_generation == generation) ReportError(error); });
    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; Raise(property); }
    private void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _refreshTimer.Stop(); await DisconnectAsync();
    }
}
