using System.Text.Json;
using DiagnosticsVisualizer.Transport;
internal static class NativeControlCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 4 || !Guid.TryParse(args[1], out var session) || !int.TryParse(args[2], out var port))
                throw new ArgumentException("NativeCapture control <session-guid> <loopback-port> status|pause|resume|stop|revert. Token: AVTRACE_NATIVE_CONTROL_TOKEN.");
            var operation = args[3] switch { "status" => NativeControlOperation.Status, "pause" => NativeControlOperation.Pause, "resume" => NativeControlOperation.Resume,
                "stop" => NativeControlOperation.Stop, "revert" => NativeControlOperation.StopAndRevert, _ => throw new ArgumentException("Unknown control operation; detach/unload is not supported.") };
            using var client = new NativeControlClient(port, Environment.GetEnvironmentVariable("AVTRACE_NATIVE_CONTROL_TOKEN") ?? throw new ArgumentException("Set AVTRACE_NATIVE_CONTROL_TOKEN."));
            var response = await client.SendAsync(session, operation);
            Console.WriteLine(JsonSerializer.Serialize(response));
            return response.HResult == 0 ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or ArgumentException or TimeoutException or System.Net.Sockets.SocketException)
        { Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}"); return 1; }
    }
}
