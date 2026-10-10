using System.Globalization;

namespace DiagnosticsVisualizer.Runtime;

public sealed record CaptureOptions
{
    public int EventsPerThread { get; init; } = 4096;
    public int MaxThreads { get; init; } = 128;
    public int MaxObjects { get; init; } = 100_000;
    public int MaxSymbols { get; init; } = 65_536;
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
            SubsystemMask = mask
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
