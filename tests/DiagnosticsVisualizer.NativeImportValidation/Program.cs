using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

if (args.Length != 3 || args[0] is not ("file" or "remote")) throw new ArgumentException("Use file <capture> <report> or remote <websocket-url> <report>.");
var store = new TraceStore(3_000_000); var acknowledged = false; var observedBeforeFooter = false; var hasFooter = false;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
if (args[0] == "file")
{
    await using var input = File.OpenRead(args[1]);
    await NativeTraceFile.ReadAsync(input, b => { Add(b); return ValueTask.CompletedTask; }, false, deadline.Token);
}
else
{
    var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("AVTRACE_TOKEN is required.");
    await using var connection = await WebSocketFrameConnection.ConnectAsync(new(args[1]), token, false, deadline.Token);
    await connection.SendAsync(CommandCodec.Encode(new(777, CommandKind.Ping)), deadline.Token);
    var decoder = new TraceStreamDecoder();
    while (!hasFooter || !acknowledged)
    {
        var frame = await connection.ReceiveAsync(deadline.Token) ?? throw new IOException("Viewer connection closed before validation.");
        if (WireEnvelope.GetKind(frame) is FrameKind.Trace or FrameKind.SnapshotDelta) Add(decoder.Decode(frame));
        else
        {
            var response = CommandCodec.Decode(frame);
            if (response.RequestId == 777 && response.Argument == "PONG") acknowledged = true;
            if (response.Argument == "SOURCE_DISCONNECTED" && !hasFooter) throw new IOException("Native source disconnected before its terminal frame.");
        }
    }
}
var trace = store.GetView(); var index = new NativeCaptureIndex(trace);
if (index.Process is null || index.End is not { HasFooter: true, DroppedRecords: 0, RecorderFaults: 0, IoFaults: 0, RejitErrors: 0 } ||
    index.InvalidGroups != 0 || index.PairedSpans < 100 || index.Methods.Length != 11 || index.Threads.Length < 4 || trace.MissingBlocks != 0 || !observedBeforeFooter)
    throw new InvalidDataException("Native import, late-join metadata or integrity validation failed.");
if (args[0] == "file" && (index.UnmatchedEntries != index.End.OpenFrames || index.UnmatchedCompletions != 0))
    throw new InvalidDataException("Recorded open-frame accounting differs from the full reader.");
using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, trace, deadline.Token); output.Position = 0;
var restored = new TraceStore(3_000_000); await TraceImport.ReadAsync(output, b => { restored.Add(b); return ValueTask.CompletedTask; }, false, deadline.Token);
var copy = restored.GetView();
if (!trace.Events.SequenceEqual(copy.Events) || trace.NativeMetadata.Count != copy.NativeMetadata.Count || trace.NativeMetadata.Any(p => !copy.NativeMetadata.TryGetValue(p.Key, out var m) || m != p.Value))
    throw new InvalidDataException("Binary export lost native events or provenance.");
var evidence = new { status = "passed", mode = args[0], events = trace.Events.Length, index.PairedSpans, index.InvalidGroups,
    index.UnmatchedEntries, index.UnmatchedCompletions, index.Incomplete, methods = index.Methods.Length, threads = index.Threads.Length,
    index.End, trace.MissingBlocks, pingAcknowledged = acknowledged, eventsBeforeFooter = observedBeforeFooter, binaryRoundtrip = true };
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
Console.WriteLine(JsonSerializer.Serialize(evidence));
void Add(TraceBlock block)
{
    if (block.Events.Length > 0 && !hasFooter) observedBeforeFooter = true;
    store.Add(block);
    foreach (var symbol in block.Symbols)
        if (NativeMetadataSymbol.TryDecode(symbol.Name, out var m) && m is NativeCaptureEnd { HasFooter: true }) hasFooter = true;
}
