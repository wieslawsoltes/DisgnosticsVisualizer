using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Agent.Avalonia;

public sealed record AgentOptions
{
    public CaptureOptions Capture { get; init; } = new();
    public string? TracePath { get; init; }
    public Uri? Endpoint { get; init; }
    public string? Token { get; init; }
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(25);
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SnapshotSliceBudget { get; init; } = TimeSpan.FromMilliseconds(2);
    public int MaxObservedObjects { get; init; } = 20_000;
    public bool RevealPrimitiveValues { get; init; }
    public bool RevealTextValues { get; init; }
    public bool ObserveNotifications { get; init; } = true;
    internal void Validate()
    {
        if (FlushInterval < TimeSpan.FromMilliseconds(5) || FlushInterval > TimeSpan.FromSeconds(5) ||
            SnapshotInterval < TimeSpan.FromMilliseconds(100) || SnapshotSliceBudget <= TimeSpan.Zero ||
            SnapshotSliceBudget > TimeSpan.FromMilliseconds(10) || MaxObservedObjects is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(AgentOptions));
        if (Endpoint is not null)
        {
            NetworkSecurity.ValidateEndpoint(Endpoint, "tcp", "tls", "ws", "wss");
            NetworkSecurity.ValidateToken(Token ?? "");
        }
        if (TracePath is not null && string.IsNullOrWhiteSpace(TracePath)) throw new ArgumentException("Invalid trace path.");
    }
}

public sealed record AgentStatus(bool Recording, long CapturedBlocks, long NetworkDroppedBlocks,
    string Connection, string? LastError);
