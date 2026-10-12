using static CaptureChecks;
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
    var investigation = VerifyInvestigation(live);
    var resourceLookups = VerifyResourceLookups(live);
    var inspection = ReferenceInspection(live); var candidates = VerifyResourceMetadata(inspection);
    var evidence = new { Styling = VerifyStyling(live), ResourceLookups = resourceLookups, Investigation = investigation, decoder.DeltaFrames, Blocks = blocks, Events = live.Events.Length, Nodes = inspection.Snapshot.Nodes.Length, LatestSnapshotNodes = live.Snapshot.Nodes.Length,
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
var investigationReport = VerifyInvestigation(view);
var resourceLookupReport = VerifyResourceLookups(view);
var inspectionView = ReferenceInspection(view);
var resourceCandidates = VerifyResourceMetadata(inspectionView);
var observed = view.Events.GroupBy(e => e.Subsystem).ToDictionary(g => g.Key.ToString(), g => g.Count());
var report = new
{
    Styling = VerifyStyling(view), ResourceLookups = resourceLookupReport, Investigation = investigationReport, read.Blocks, read.Events, read.TruncatedTail, read.DeltaFrames, view.DroppedEvents, view.ProbeFaults,
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


static object VerifyStyling(TraceView trace)
{
    var index = new StyleEvaluationIndex(trace);
    var target = trace.Checkpoints.SelectMany(c => c.Data.Nodes).FirstOrDefault(n => n.Name == "StyleProbe")?.Id ?? 0;
    if (target == 0) throw new InvalidDataException("Styling reference target checkpoint was not retained.");
    var attachments = index.Calls.Where(r => r.Kind == StyleEvaluationKind.Attach && r.TargetId == target).ToArray();
    var styles = attachments.Select(r => r.SubjectId).ToHashSet();
    var frames = index.Calls.Where(r => r.Kind == StyleEvaluationKind.Frame && styles.Contains(r.ContextId)).ToArray();
    if (!attachments.Any(r => r.Status == "Conditional (activator)") ||
        !index.Calls.Any(r => r.Kind == StyleEvaluationKind.Match && r.TargetId == target && r.ActivatorId > 0) ||
        !frames.Any(r => r.Status == "Active" && r.Changed == true) || !frames.Any(r => r.Status == "Inactive" && r.Changed == true) ||
        !index.Calls.Any(r => r.Kind == StyleEvaluationKind.Signal && styles.Contains(r.ContextId)) || index.InvalidMarkers != 0)
        throw new InvalidDataException("Original selector results and observed reference style activation were not preserved.");
    return new { Evaluations = index.Calls.Length, Roots = index.Roots.Length, index.InvalidMarkers,
        Conditional = index.Calls.Count(r => r.Status == "Conditional (activator)"),
        FrameChanges = index.Calls.Count(r => r.Kind == StyleEvaluationKind.Frame && r.Changed == true),
        Signals = index.Calls.Count(r => r.Kind == StyleEvaluationKind.Signal), ReferenceTarget = target,
        ReferenceAttachments = attachments.Length, ReferenceFrameObservations = frames.Length };
}
