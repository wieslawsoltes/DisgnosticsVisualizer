namespace DiagnosticsVisualizer.Weaver;

public sealed record WeaveOptions(string Directory)
{
    public string? EntryAssembly { get; init; }
    public string[] Include { get; init; } = [];
    public bool Deep { get; init; }
    public bool AllowSignedRewrite { get; init; }
    public string? SigningKey { get; init; }
    public bool Bootstrap { get; init; } = true;
}
public sealed record MethodReport(int Token, string Signature, string Category,
    string Status, int NotificationSites = 0, string? Note = null);
public sealed record AssemblyReport(string File, string OriginalSha256, string OutputSha256,
    string OriginalMvid, string OutputMvid, string Version, bool SignedIdentity,
    bool Bootstrapped, MethodReport[] Methods, string Status);
public sealed record WeaveManifest(string Schema, string Profile, AssemblyReport[] Assemblies);
