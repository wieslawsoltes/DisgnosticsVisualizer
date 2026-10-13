namespace DiagnosticsVisualizer.Core;

/// <summary>Wire-stable subsystem identifiers. Append values; never renumber.</summary>
public enum Subsystem : byte
{
    Layout, Styles, Selectors, Resources, Templates, Properties, Dispatcher,
    RoutedEvents, Xaml, Bindings, PropertyNotifications, CollectionNotifications,
    VisualTree, LogicalTree, Rendering, Compositor, Animation, DataTemplates,
    ControlThemes, Application, Diagnostics
}

public enum TraceEventKind : byte { Span, Instant, Counter, Link }
public enum CommandKind : byte
{
    Authenticate, RequestSnapshot, SelectObject, Pause, Resume, Ping, Response, Capabilities
}

/// <summary>All times use the source process' monotonic clock, not wall time.</summary>
public readonly record struct TraceEvent(
    long Id, long ParentId, long Timestamp, long Duration, long ObjectId,
    long RelatedId, int ThreadId, int NameId, Subsystem Subsystem,
    TraceEventKind Kind = TraceEventKind.Span, long Data0 = 0, long Data1 = 0)
{
    public long End => Timestamp > long.MaxValue - Duration ? long.MaxValue : Timestamp + Duration;
}

public sealed record Symbol(int Id, string Name);
public sealed record ObjectInfo(long Id, string Type, string Display);
public sealed record NodeSnapshot(long Id, long VisualParent, long LogicalParent,
    long TemplatedParent, string Type, string Name, double X, double Y,
    double Width, double Height, bool Visible, string Classes);
public sealed record PropertySnapshot(long ObjectId, string Name, string Kind,
    string Value, string Priority, string Source);
public sealed record RelationSnapshot(long Source, long Target, string Kind, string Detail);
public sealed record SnapshotData(NodeSnapshot[] Nodes, PropertySnapshot[] Properties,
    RelationSnapshot[] Relations, long SelectedObject = 0, bool Truncated = false)
{
    public static SnapshotData Empty { get; } = new([], [], []);
}

/// <summary>
/// A independently decodable batch. Symbol/object definitions used by its events
/// travel with the batch. Snapshots are full checkpoints, not lossy inter-packet deltas.
/// </summary>
public sealed record TraceBlock
{
    public Guid SessionId { get; init; }
    public long Sequence { get; init; }
    public long Frequency { get; init; } = 10_000_000;
    public long OriginTimestamp { get; init; }
    public long UtcStartTicks { get; init; } = DateTime.UtcNow.Ticks;
    public long DroppedEvents { get; init; }
    public long ProbeFaults { get; init; }
    public long MetadataDrops { get; init; }
    public bool Recording { get; init; } = true;
    public Symbol[] Symbols { get; init; } = [];
    public ObjectInfo[] Objects { get; init; } = [];
    public TraceEvent[] Events { get; init; } = [];
    public SnapshotData? Snapshot { get; init; }
}

public sealed record TraceCommand(long RequestId, CommandKind Kind,
    long ObjectId = 0, string Argument = "");

public static class TraceLimits
{
    public const int MaxFrameBytes = 8 * 1024 * 1024;
    public const int MaxEventsPerBlock = 8192;
    public const int MaxSymbolsPerBlock = 8192;
    public const int MaxObjectsPerBlock = 16384;
    public const int MaxNodesPerSnapshot = 8192;
    public const int MaxPropertiesPerSnapshot = 4096;
    public const int MaxRelationsPerSnapshot = 16384;
    public const int MaxStringBytes = 16 * 1024;
    public const int MaxCommandBytes = 4096;
}
