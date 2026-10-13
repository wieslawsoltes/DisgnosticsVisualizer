using System.Globalization;
using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Transport;

internal static class NativeCaptureCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (args[0] == "convert")
            {
                if (args.Length != 3) throw new ArgumentException("NativeCapture convert <recording.dvntrace> <new-output.avtrace>");
                var destination = Path.GetFullPath(args[2]);
                if (File.Exists(destination)) throw new IOException("The output already exists; it will not be overwritten.");
                await using var input = OpenInput(args[1]);
                var store = new TraceStore(3_000_000);
                var read = await NativeTraceFile.ReadAsync(input, b => { store.Add(b); return ValueTask.CompletedTask; }, false, stop.Token);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                    if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var output = new FileStream(temporary, fileOptions))
                        await TraceExport.WriteBinaryAsync(output, store.GetView(), stop.Token);
                    stop.Token.ThrowIfCancellationRequested(); File.Move(temporary, destination, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                var index = new NativeCaptureIndex(store.GetView());
                Console.WriteLine(JsonSerializer.Serialize(new { status = "converted", read.Events, index.PairedSpans, index.InvalidGroups, index.Incomplete, destination }));
                return 0;
            }
            if (args.Length < 3) throw new ArgumentException("NativeCapture relay <recording.dvntrace> <tcp://host:port|tls://host:port> [--follow] [--attempts 3] [--idle-ms 30000]");
            var follow = false; var attempts = 3; var idle = 30000; var controlPort = 0;
            for (var i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--control-port" when i + 1 < args.Length: controlPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--follow" when !follow: follow = true; break;
                    case "--attempts" when i + 1 < args.Length: attempts = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--idle-ms" when i + 1 < args.Length: idle = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException("Unknown or incomplete relay option.");
                }
            }
            var token = Environment.GetEnvironmentVariable("AVTRACE_TOKEN") ?? throw new ArgumentException("Set AVTRACE_TOKEN; credentials are never accepted in the URL or arguments.");
            using var control = controlPort == 0 ? null : new NativeControlClient(controlPort, Environment.GetEnvironmentVariable("AVTRACE_NATIVE_CONTROL_TOKEN") ?? throw new ArgumentException("Set AVTRACE_NATIVE_CONTROL_TOKEN."));
            await using var source = OpenInput(args[1]);
            var result = await NativeRelayClient.RelayAsync(source, new Uri(args[2], UriKind.Absolute), token,
                new() { Follow = follow, TargetControl = control, MaximumAttempts = attempts, IdleTimeout = TimeSpan.FromMilliseconds(idle) }, stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "relayed", result.Capture.Events, result.Attempts, result.FramesSent,
                result.UniqueDecodedFrames, result.ReplayedFrames, note = "Reliable writes, not end-to-end acknowledgements. Reconnect replay preserves session/sequence. Target control is explicit opt-in; canceling the relay does not stop the target." }));
            return 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Native sidecar canceled; this does not stop or unload the target profiler."); return 130; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or TimeoutException or System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException)
        { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}"); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private static FileStream OpenInput(string path) => new(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
