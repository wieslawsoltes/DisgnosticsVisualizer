using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

if (args.Length != 3 || args[0] is not ("offline" or "viewer" or "late" or "file" or "remote"))
    throw new ArgumentException("NativeImportValidation offline|viewer|late <file-or-websocket-url> <report.json>");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var store = new TraceStore(3_000_000); var commands = new Dictionary<long, string>(); long blocks = 0; var observedBeforeFooter = false; var hasFooter = false;
if (args[0] is "offline" or "file")
{
    await using var input = File.OpenRead(args[1]);
    await NativeTraceFile.ReadAsync(input, b => { store.Add(b); blocks++; return ValueTask.CompletedTask; }, false, deadline.Token);
    using var binary = new MemoryStream(); await TraceExport.WriteBinaryAsync(binary, store.GetView(), deadline.Token); binary.Position = 0;
    var restored = new TraceStore(3_000_000);
    await TraceImport.ReadAsync(binary, b => { restored.Add(b); return ValueTask.CompletedTask; }, false, deadline.Token);
    if (!store.GetView().Events.SequenceEqual(restored.GetView().Events) || !store.GetView().NativeMetadata.OrderBy(p=>p.Key).SequenceEqual(restored.GetView().NativeMetadata.OrderBy(p=>p.Key)))
        throw new InvalidDataException("Native export changed event or provenance content.");
}
else
{
    var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("Set AVTRACE_TOKEN");
    await using var connection = await WebSocketFrameConnection.ConnectAsync(new(args[1]), token, false, deadline.Token);
    var decoder = new TraceStreamDecoder(); bool sent = false, announcedReady = false;
    while (await connection.ReceiveAsync(deadline.Token) is { } frame)
    {
        if (WireEnvelope.GetKind(frame) is FrameKind.Trace or FrameKind.SnapshotDelta)
        {
            var block = decoder.Decode(frame);
            if (block.Events.Length > 0 && !hasFooter) observedBeforeFooter = true;
            store.Add(block); blocks++;
            hasFooter |= block.Symbols.Any(s => NativeMetadataSymbol.TryDecode(s.Name, out var m) && m is NativeCaptureEnd { HasFooter: true });
            if (!sent && args[0] is "viewer" or "remote")
            {
                await connection.SendAsync(CommandCodec.Encode(new(101, CommandKind.Ping)), deadline.Token);
                await connection.SendAsync(CommandCodec.Encode(new(102, CommandKind.Pause)), deadline.Token);
                await connection.SendAsync(CommandCodec.Encode(new(103, CommandKind.RequestSnapshot)), deadline.Token); sent = true;
            }
        }
        else
        {
            var reply = CommandCodec.Decode(frame); commands[reply.RequestId] = reply.Argument;
            if (reply.Argument == "SOURCE_DISCONNECTED" && blocks > 0 || args[0] == "late" && reply.Argument == "NO_SOURCE" && blocks > 0) break;
        }
        // The segmented replay test must not resume its producer merely because
        // the server accepted this socket. Signal actual pre-footer consumption
        // and all three authenticated round trips, after the bootstrap is read.
        if (!announcedReady && args[0] == "remote" && observedBeforeFooter && !hasFooter &&
            commands.GetValueOrDefault(101) == "PONG" && commands.GetValueOrDefault(102) == "UNSUPPORTED_NATIVE_CONTROL" &&
            commands.GetValueOrDefault(103) == "UNSUPPORTED_NATIVE_SNAPSHOT")
        {
            Console.WriteLine("NATIVE_VIEWER_READY"); Console.Out.Flush(); announcedReady = true;
        }
    }
    if (args[0] is "viewer" or "remote" && (commands.GetValueOrDefault(101) != "PONG" || commands.GetValueOrDefault(102) != "UNSUPPORTED_NATIVE_CONTROL" || commands.GetValueOrDefault(103) != "UNSUPPORTED_NATIVE_SNAPSHOT"))
        throw new InvalidDataException("Native control capability responses were not preserved through the collector.");
}
var view = store.GetView(); var index = new NativeCaptureIndex(view);
if (index.Process is null || index.End?.HasFooter != true || index.Methods.Length != 11 || index.Threads.Length < 4 || index.InvalidGroups != 0 || index.PairedSpans == 0)
    throw new InvalidDataException("Native coverage/provenance failed end-to-end validation.");
if (args[0] is not ("late" or "remote") && (view.DroppedEvents != 0 || view.ProbeFaults != 0 || view.MissingBlocks != 0))
    throw new InvalidDataException("Reference native transport lost recorded data.");
using (var binary = new MemoryStream())
{
    await TraceExport.WriteBinaryAsync(binary, view, deadline.Token); binary.Position = 0;
    var restored = new TraceStore(3_000_000);
    await TraceImport.ReadAsync(binary, b => { restored.Add(b); return ValueTask.CompletedTask; }, false, deadline.Token);
    if (!view.Events.SequenceEqual(restored.GetView().Events) || !view.NativeMetadata.OrderBy(p => p.Key).SequenceEqual(restored.GetView().NativeMetadata.OrderBy(p => p.Key)))
        throw new InvalidDataException("Export changed native events or provenance.");
}
var result = new { eventsBeforeFooter = observedBeforeFooter, pingAcknowledged = commands.GetValueOrDefault(101) == "PONG", binaryRoundtrip = true, status = "passed", mode = args[0], blocks, events = view.Events.Length,
    eventSha256 = EventDigest(view), provenanceSha256 = ProvenanceDigest(view), index.PairedSpans,
    index.UnmatchedEntries, index.UnmatchedCompletions, index.InvalidGroups, index.Incomplete,
    view.DroppedEvents, view.ProbeFaults, view.MissingBlocks, definitions = index.Methods.Length, threads = index.Threads.Length, commands };
await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
Console.WriteLine(JsonSerializer.Serialize(result));

// Canonical content fingerprints compare actual live/offline payloads, not just counts.
static string EventDigest(TraceView view)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Span<long> values = stackalloc long[12]; Span<byte> bytes = stackalloc byte[12 * 8];
    foreach (var e in view.Events)
    {
        values[0] = e.Id; values[1] = e.ParentId; values[2] = e.Timestamp; values[3] = e.Duration;
        values[4] = e.ObjectId; values[5] = e.RelatedId; values[6] = e.ThreadId; values[7] = e.NameId;
        values[8] = (long)e.Subsystem; values[9] = (long)e.Kind; values[10] = e.Data0; values[11] = e.Data1;
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(i * 8, 8), values[i]);
        hash.AppendData(bytes);
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}
static string ProvenanceDigest(TraceView view)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    Add(view.SessionId.ToString("D"));
    Add(view.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Add(view.OriginTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Add(view.UtcStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
    foreach (var p in view.SourceSites.OrderBy(p => p.Key))
    { Add(p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)); Add(SourceSiteSymbol.Encode(p.Value)); }
    foreach (var p in view.NativeMetadata.OrderBy(p => p.Key)) Add(NativeMetadataSymbol.Encode(p.Value));
    return Convert.ToHexString(hash.GetHashAndReset());
    void Add(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text); Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length); hash.AppendData(length); hash.AppendData(payload);
    }
}
