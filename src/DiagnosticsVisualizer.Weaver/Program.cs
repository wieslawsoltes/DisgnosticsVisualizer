using DiagnosticsVisualizer.Weaver;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h")
    {
        Console.WriteLine("avtrace weave|restore --directory <app-output> [--entry App.dll] [--include A.dll;B.dll] [--deep] [--allow-signed] [--key original.snk] [--no-bootstrap]");
        return 0;
    }
    string? Value(string flag)
    {
        var i = Array.IndexOf(args, flag);
        if (i < 0) return null;
        if (i == args.Length - 1 || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Missing value for {flag}.");
        return args[i + 1];
    }
    var directory = Value("--directory") ?? throw new ArgumentException("--directory is required.");
    if (args[0] == "restore") { WeaveEngine.Restore(directory); Console.WriteLine("Restored unchanged instrumented outputs."); return 0; }
    if (args[0] != "weave") throw new ArgumentException("Unknown command.");
    var report = WeaveEngine.Weave(new(directory)
    {
        EntryAssembly = Value("--entry"), Include = (Value("--include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries),
        Deep = args.Contains("--deep"), AllowSignedRewrite = args.Contains("--allow-signed"), SigningKey = Value("--key"),
        Bootstrap = !args.Contains("--no-bootstrap")
    });
    foreach (var assembly in report.Assemblies)
        Console.WriteLine($"{assembly.File}: {assembly.Methods.Count(m => m.Status == "Instrumented")} spans; {assembly.Methods.Sum(m => m.NotificationSites)} notification sites; {assembly.Status}");
    Console.WriteLine("Wrote avtrace.manifest.json. Instrumented copies are diagnostic artifacts, not production binaries.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"avtrace: {exception.GetType().Name}: {exception.Message}"); return 1;
}
