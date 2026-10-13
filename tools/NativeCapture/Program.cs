using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

if (args.Length == 1 && args[0] is "--help" or "help")
{
    Console.WriteLine("NativeCapture inspect <file> | list | attach <pid> <profiler> <new-capture> <duration-ms> <Assembly!Type::Method> [...] | convert <native-file> <new-avtrace-file> [--recover] | relay <native-file> <tcp-or-tls-endpoint> [--follow] [--attempts 3] [--idle-ms 30000]");
    Console.WriteLine("Relay credentials: AVTRACE_TOKEN. Ctrl+C cancels the sidecar, not the target profiler."); return 0;
}

if (args.Length > 0 && (args[0] == "inspect" || args[0] == "convert" && args.Length == 4 ||
    args[0] == "relay" && args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal)))
    return await CaptureCommands.RunAsync(args);
if (args.Length > 0 && args[0] is "convert" or "relay")
    return await NativeCaptureCommands.RunAsync(args);

if (args.Length == 1 && args[0] == "list")
{
    Console.WriteLine(JsonSerializer.Serialize(DiagnosticsClient.GetPublishedProcesses().Order())); return 0;
}
if (args.Length < 6 || args[0] != "attach")
{
    Console.Error.WriteLine("NativeCapture inspect <file> | list | attach <pid> <profiler-path> <new-capture-path> <duration-ms> <Assembly!Type::Method> [more selectors]"); return 2;
}
try
{
    var pid = int.Parse(args[1], CultureInfo.InvariantCulture);
    var duration = int.Parse(args[4], CultureInfo.InvariantCulture);
    if (pid <= 0 || duration is < 100 or > 3600000) throw new ArgumentException("Invalid process or duration.");
    var profiler = Path.GetFullPath(args[2]); var capture = Path.GetFullPath(args[3]);
    if (!File.Exists(profiler) || File.Exists(capture)) throw new ArgumentException("Profiler must exist; capture must not exist.");
    if (!Directory.Exists(Path.GetDirectoryName(capture))) throw new ArgumentException("Capture directory must exist.");
    var rules = args[5..]; if (rules.Length is < 1 or > 64) throw new ArgumentException("Specify 1..64 exact method selectors.");
    foreach (var rule in rules) if (!rule.Contains('!') || !rule.Contains("::") || rule.Contains('*') || Encoding.UTF8.GetByteCount(rule) > 1024 || rule.Contains('\0')) throw new ArgumentException("Invalid exact method selector.");
    using var payload = new MemoryStream(); using var writer = new BinaryWriter(payload, new UTF8Encoding(false, true), true);
    writer.Write("DVNP0001"u8); writer.Write(duration); writer.Write(1); writer.Write(DateTime.UtcNow.Ticks); var session = Guid.NewGuid(); writer.Write(session.ToByteArray());
    void Text(string text) { var bytes = Encoding.UTF8.GetBytes(text); if (bytes.Length > 4096 || text.Contains('\0')) throw new ArgumentException("Path is too long or contains a null."); writer.Write(bytes.Length); writer.Write(bytes); }
    Text(capture); writer.Write(rules.Length); foreach (var rule in rules) Text(rule);
    var bytes = payload.ToArray(); if (bytes.Length > 60000) throw new ArgumentException("Configuration too large.");
    new DiagnosticsClient(pid).AttachProfiler(TimeSpan.FromSeconds(15), Guid.Parse("6eadfa34-8f22-4e0b-9685-729b1124d360"), profiler, bytes);
    Console.WriteLine(JsonSerializer.Serialize(new { status = "attached", pid, session, capture, durationMs = duration, rules,
        note = "Attachment success does not prove instrumentation coverage; inspect the recording's per-method status and terminal integrity." })); return 0;
}
catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
