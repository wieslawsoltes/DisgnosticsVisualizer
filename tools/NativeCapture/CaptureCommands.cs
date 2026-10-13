using System.Globalization;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

internal static class CaptureCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args.Length < 2) throw new ArgumentException("A recording path is required.");
            var path = Path.GetFullPath(args[1]);
            FileStream Open() => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            switch (args[0])
            {
                case "inspect" when args.Length == 2:
                    await using (var input = Open())
                    {
                        var store = new TraceStore();
                        var read = await TraceImport.ReadAsync(input, b => { store.Add(b); return ValueTask.CompletedTask; }, true, stop.Token);
                        var trace = store.GetView(); var index = new NativeCaptureIndex(trace);
                        if (index.Process is null) throw new InvalidDataException("No native provenance in this recording.");
                        Console.WriteLine(JsonSerializer.Serialize(new { status = "inspected", read.Blocks, read.Events, read.TruncatedTail,
                            trace.SessionId, index.Process, index.End, index.PairedSpans, index.InvalidGroups, index.UnmatchedEntries,
                            index.UnmatchedCompletions, index.Incomplete, index.Methods, index.Threads }));
                    }
                    return 0;
                case "convert" when args.Length is 3 or 4:
                    if (args.Length == 4 && args[3] != "--recover") throw new ArgumentException("Only --recover is accepted after the output path.");
                    var output = Path.GetFullPath(args[2]);
                    if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists; it will not be overwritten.");
                    var temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try
                    {
                        TraceReadResult read;
                        await using (var input = Open())
                        await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                        {
                            read = await TraceImport.ReadAsync(input, b => TraceFile.WriteAsync(destination, b, stop.Token), args.Length == 4, stop.Token);
                            await destination.FlushAsync(stop.Token);
                        }
                        stop.Token.ThrowIfCancellationRequested(); File.Move(temporary, output, false);
                        Console.WriteLine(JsonSerializer.Serialize(new { status = "converted", output, read.Blocks, read.Events, read.TruncatedTail }));
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return 0;
                case "relay" when args.Length is >= 3 and <= 5:
                    var endpoint = new Uri(args[2], UriKind.Absolute);
                    var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("Set AVTRACE_TOKEN for collector authentication.");
                    NetworkSecurity.ValidateEndpoint(endpoint, "tcp", "tls"); NetworkSecurity.ValidateToken(token);
                    var idle = args.Length >= 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 30000;
                    var retries = args.Length == 5 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 3;
                    var result = await NativeCaptureRelay.RunWithReconnectAsync(Open,
                        async ct => await StreamFrameConnection.ConnectAsync(endpoint, token, true, ct), TimeSpan.FromMilliseconds(idle), retries, stop.Token);
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "forwarded", result.Capture, result.Connections, result.Reconnects,
                        note = "Reliable at-least-once forwarding; replay uses stable identities. Cancellation stops the sidecar, not the target recorder. Native pause/snapshot controls are unsupported." }));
                    return 0;
                default: throw new ArgumentException("Use inspect <file>, convert <file> <new-output> [--recover], or relay <file> <tcp/tls-url> [idle-ms] [retries].");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Console.Error.WriteLine("Canceled; the native target retains its configured duration."); return 130; }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
