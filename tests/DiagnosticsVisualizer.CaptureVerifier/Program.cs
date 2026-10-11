using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

if (args.Length == 3 && args[0] == "--remote")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("Set AVTRACE_TOKEN.");
    await using var connection = await WebSocketFrameConnection.ConnectAsync(new(args[1]), token, false, timeout.Token);
    var liveStore = new TraceStore(1_000_000); long blocks = 0; var requested = false; var acknowledged = false;
    var decoder = new TraceStreamDecoder();
    while (await connection.ReceiveAsync(timeout.Token) is { } frame)
    {
        if (WireEnvelope.GetKind(frame) is FrameKind.Trace or FrameKind.SnapshotDelta)
        {
            liveStore.Add(decoder.Decode(frame)); blocks++;
            if (!requested)
            {
                await connection.SendAsync(CommandCodec.Encode(new(42, CommandKind.RequestSnapshot)), timeout.Token);
                requested = true;
            }
        }
        else
        {
            var command = CommandCodec.Decode(frame);
            if (command.RequestId == 42 && command.Argument == "OK") acknowledged = true;
            if (command.Argument == "SOURCE_DISCONNECTED" && blocks > 0) break;
        }
    }
    var live = liveStore.GetView();
    var causalNotifications = CountAsyncNotifications(live);
    var lifetimes = VerifiedLifetimes(live);
    var inspection = ReferenceInspection(live); var candidates = VerifyResourceMetadata(inspection);
    var evidence = new { decoder.DeltaFrames, Blocks = blocks, Events = live.Events.Length, Nodes = inspection.Snapshot.Nodes.Length, LatestSnapshotNodes = live.Snapshot.Nodes.Length,
        ResourceCandidates = candidates, AsyncLifetimes = lifetimes, AsyncCausalNotifications = causalNotifications, SourceSites = live.SourceSites.Count, MappedSites = live.SourceSites.Values.Count(s => s.Line > 0),
        live.ProbeFaults, live.DroppedEvents, live.MissingBlocks, CommandAcknowledged = acknowledged };
    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    if (live.Events.Length == 0 || inspection.Snapshot.Nodes.Length == 0 || live.ProbeFaults != 0 || !acknowledged || causalNotifications == 0)
        throw new InvalidDataException("The real target-to-collector-to-WebSocket viewer/control loop failed validation.");
    Console.WriteLine(JsonSerializer.Serialize(evidence));
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: CaptureVerifier <trace.avtrace> <report.json>, or --remote <websocket-url> <report.json>");
var store = new TraceStore(1_000_000);
await using var stream = File.OpenRead(args[0]);
var read = await TraceFile.ReadAsync(stream, block => { store.Add(block); return ValueTask.CompletedTask; }, false);
var view = store.GetView();
var causalCount = CountAsyncNotifications(view);
var completedLifetimes = VerifiedLifetimes(view);
var inspectionView = ReferenceInspection(view);
var resourceCandidates = VerifyResourceMetadata(inspectionView);
var observed = view.Events.GroupBy(e => e.Subsystem).ToDictionary(g => g.Key.ToString(), g => g.Count());
var report = new
{
    read.Blocks, read.Events, read.TruncatedTail, read.DeltaFrames, view.DroppedEvents, view.ProbeFaults,
    view.MetadataDrops, view.MissingBlocks,
    SourceSites = view.SourceSites.Count, MappedSites = view.SourceSites.Values.Count(s => s.Line > 0),
    AsyncKickoffSites = view.SourceSites.Values.Count(s => s.AsyncKickoff),
    ResourceCandidates = resourceCandidates, AsyncLifetimes = completedLifetimes, AsyncCausalNotifications = causalCount,
    Nodes = inspectionView.Snapshot.Nodes.Length, Properties = inspectionView.Snapshot.Properties.Length, LatestSnapshotNodes = view.Snapshot.Nodes.Length,
    Subsystems = observed, Findings = TraceAnalyzer.Analyze(view).Findings
};
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (read.Events == 0 || read.TruncatedTail || view.ProbeFaults != 0 || inspectionView.Snapshot.Nodes.Length == 0)
    throw new InvalidDataException("Instrumented target did not produce a valid, fault-free capture and tree checkpoint.");
if (causalCount == 0) throw new InvalidDataException("No cross-thread notification linked to a real application async kickoff.");
foreach (var required in Enum.GetValues<Subsystem>())
    if (!observed.ContainsKey(required.ToString())) throw new InvalidDataException($"No executed probe for {required}.");
if (!view.SourceSites.Values.Any(s => s.Assembly == "DiagnosticsVisualizer.Sample" && s.Line > 0) ||
    !view.SourceSites.Values.Any(s => s.AsyncKickoff)) throw new InvalidDataException("Missing real source mapping or async kickoff coverage.");
Console.WriteLine(JsonSerializer.Serialize(report));

// Verify observed causality, not merely the presence of an async method name.
static int CountAsyncNotifications(TraceView view)
{
    var events = view.Events.ToDictionary(e => e.Id);
    var count = 0;
    foreach (var notification in view.Events.Where(e => e.Subsystem == Subsystem.PropertyNotifications))
    {
        var current = notification;
        var seen = new HashSet<long>();
        while (current.ParentId != 0 && seen.Add(current.Id) && events.TryGetValue(current.ParentId, out current))
        {
            if (current.ThreadId != notification.ThreadId &&
                view.SourceSites.TryGetValue(current.NameId, out var site) && site.AsyncKickoff &&
                site.Assembly == "DiagnosticsVisualizer.Sample")
            { count++; break; }
        }
    }
    return count;
}

static int VerifiedLifetimes(TraceView view)
{
    var lifetimes = AsyncOperations.Analyze(view);
    var completed = lifetimes.Count(operation => operation.IsComplete && operation.Begin is { } begin &&
        view.SourceSites.TryGetValue(begin.NameId, out var site) && site.Assembly == "DiagnosticsVisualizer.Sample");
    if (completed == 0) throw new InvalidDataException("No completed, source-identified application async lifetime.");
    return completed;
}

static int VerifyResourceMetadata(TraceView view)
{
    var candidates = view.Snapshot.Properties.Where(p => p.Kind == "Resource candidate").ToArray();
    if (!candidates.Any(p => p.Name == "DiagnosticsLazy") ||
        !view.Snapshot.Relations.Any(r => r.Kind == "Merged dictionary"))
        throw new InvalidDataException("The resource inventory did not survive capture/transport.");
    foreach (var relation in view.Snapshot.Relations)
        if (relation.Source > 0 && relation.Target > 0 && (!view.Objects.ContainsKey(relation.Source) || !view.Objects.ContainsKey(relation.Target)))
            throw new InvalidDataException("Snapshot-only relationship metadata is missing.");
    return candidates.Length;
}

static TraceView ReferenceInspection(TraceView view)
{
    // Closing a real window may produce an empty final checkpoint. Inspect the retained last
    // populated state while separately reporting latest-node count; never alter viewer state.
    return view with { Snapshot = view.Checkpoints.LastOrDefault(c => c.Data.Nodes.Length > 0)?.Data ?? view.Snapshot };
}
