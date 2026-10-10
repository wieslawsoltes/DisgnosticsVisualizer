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
    while (await connection.ReceiveAsync(timeout.Token) is { } frame)
    {
        if (WireEnvelope.GetKind(frame) == FrameKind.Trace)
        {
            liveStore.Add(TraceCodec.Decode(frame)); blocks++;
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
    var evidence = new { Blocks = blocks, Events = live.Events.Length, Nodes = live.Snapshot.Nodes.Length,
        live.ProbeFaults, live.DroppedEvents, live.MissingBlocks, CommandAcknowledged = acknowledged };
    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    if (live.Events.Length == 0 || live.Snapshot.Nodes.Length == 0 || live.ProbeFaults != 0 || !acknowledged)
        throw new InvalidDataException("The real target-to-collector-to-WebSocket viewer/control loop failed validation.");
    Console.WriteLine(JsonSerializer.Serialize(evidence));
    return;
}
if (args.Length != 2) throw new ArgumentException("Usage: CaptureVerifier <trace.avtrace> <report.json>, or --remote <websocket-url> <report.json>");
var store = new TraceStore(1_000_000);
await using var stream = File.OpenRead(args[0]);
var read = await TraceFile.ReadAsync(stream, block => { store.Add(block); return ValueTask.CompletedTask; }, false);
var view = store.GetView();
var observed = view.Events.GroupBy(e => e.Subsystem).ToDictionary(g => g.Key.ToString(), g => g.Count());
var report = new
{
    read.Blocks, read.Events, read.TruncatedTail, view.DroppedEvents, view.ProbeFaults,
    view.MetadataDrops, view.MissingBlocks,
    Nodes = view.Snapshot.Nodes.Length, Properties = view.Snapshot.Properties.Length,
    Subsystems = observed, Findings = TraceAnalyzer.Analyze(view).Findings
};
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (read.Events == 0 || read.TruncatedTail || view.ProbeFaults != 0 || view.Snapshot.Nodes.Length == 0)
    throw new InvalidDataException("Instrumented target did not produce a valid, fault-free capture and tree checkpoint.");
foreach (var required in new[] { Subsystem.Layout, Subsystem.Properties, Subsystem.Xaml, Subsystem.RoutedEvents, Subsystem.PropertyNotifications })
    if (!observed.ContainsKey(required.ToString())) throw new InvalidDataException($"No executed probe for {required}.");
Console.WriteLine(JsonSerializer.Serialize(report));
