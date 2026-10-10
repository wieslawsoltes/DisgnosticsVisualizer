using System.Net;
using DiagnosticsVisualizer.Runtime;
using DiagnosticsVisualizer.Transport;

namespace DiagnosticsVisualizer.Agent.Avalonia;

public sealed record AgentOptions
{
    /// <summary>Environment defaults are read once. An explicit Capture initializer overrides them.</summary>
    public CaptureOptions Capture { get; init; } = CaptureOptions.FromEnvironment();
    public string? TracePath { get; init; }
    public Uri? Endpoint { get; init; }
    /// <summary>Optional encrypted telemetry destination; Endpoint remains the reliable channel.</summary>
    public IPEndPoint? UdpEndpoint { get; init; }
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
        if (UdpEndpoint is not null && (Endpoint is null || UdpEndpoint.Port == 0 || OperatingSystem.IsBrowser()))
            throw new ArgumentException("UDP requires a native agent, a nonzero destination port and an authenticated reliable endpoint.");
        if (TracePath is not null && string.IsNullOrWhiteSpace(TracePath)) throw new ArgumentException("Invalid trace path.");
    }
}

public sealed record AgentStatus(bool Recording, long CapturedBlocks, long NetworkDroppedBlocks,
    string Connection, string? LastError, long UdpSentFrames = 0, long UdpSentDatagrams = 0, long UdpFailures = 0);
