using System.Globalization;

namespace DiagnosticsVisualizer.Runtime;

public sealed record CaptureOptions
{
    public int EventsPerThread { get; init; } = 4096;
    public int MaxThreads { get; init; } = 128;
    public int MaxObjects { get; init; } = 100_000;
    public int MaxSymbols { get; init; } = 65_536;
    /// <summary>Opt-in AsyncLocal causality across ExecutionContext boundaries. Adds allocations per recorded span.</summary>
    public bool FlowExecutionContext { get; init; }
    /// <summary>Opt-in standard async state-machine lifecycle markers. Allocates one weak session reference per sampled operation.</summary>
    public bool TrackAsyncOperations { get; init; }
    /// <summary>Opt-in object-to-XAML construction/population observations from portable PDBs.</summary>
    public bool TrackXamlObjects { get; init; }
    /// <summary>Opt-in admitted-queue/callback/abort markers with weak work-item state.</summary>
    public bool TrackDispatcherOperations { get; init; }
    public int SampleEvery { get; init; } = 1;
    public ulong SubsystemMask { get; init; } = ulong.MaxValue;
    /// <summary>Parse bounded startup settings once, never on a producer's hot path.</summary>
    public static CaptureOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        int Integer(string name, int fallback)
        {
            var value = read(name);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new ArgumentException($"{name} must be an unsigned decimal integer.");
            return parsed;
        }
        bool Boolean(string name)
        {
            var value = read(name);
            if (string.IsNullOrWhiteSpace(value) || value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            throw new ArgumentException($"{name} must be 0, 1, false or true.");
        }
        var mask = ulong.MaxValue;
        var text = read("AVTRACE_SUBSYSTEM_MASK");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var hexadecimal = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(hexadecimal ? text.AsSpan(2) : text.AsSpan(),
                hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out mask))
                throw new ArgumentException("AVTRACE_SUBSYSTEM_MASK must be an unsigned decimal or 0x-prefixed hexadecimal integer.");
        }
        var options = new CaptureOptions
        {
            EventsPerThread = Integer("AVTRACE_EVENTS_PER_THREAD", 4096),
            MaxThreads = Integer("AVTRACE_MAX_THREADS", 128),
            MaxObjects = Integer("AVTRACE_MAX_OBJECTS", 100_000),
            MaxSymbols = Integer("AVTRACE_MAX_SYMBOLS", 65_536),
            SampleEvery = Integer("AVTRACE_SAMPLE_EVERY", 1),
            SubsystemMask = mask,
            FlowExecutionContext = Boolean("AVTRACE_FLOW_EXECUTION_CONTEXT"),
            TrackAsyncOperations = Boolean("AVTRACE_ASYNC_OPERATIONS"),
            TrackXamlObjects = Boolean("AVTRACE_XAML_OBJECTS"),
            TrackDispatcherOperations = Boolean("AVTRACE_DISPATCHER_OPERATIONS")
        };
        options.Validate(); return options;
    }
    internal void Validate()
    {
        if (EventsPerThread is < 16 or > 1_048_576 || (EventsPerThread & (EventsPerThread - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(EventsPerThread), "Capacity must be a power of two between 16 and 1048576.");
        if (MaxThreads is < 1 or > 32767 || MaxObjects is < 1 or > 10_000_000 ||
            MaxSymbols is < 1 or > 1_000_000 || SampleEvery < 1) throw new ArgumentOutOfRangeException(nameof(CaptureOptions));
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class InstrumentedAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}
