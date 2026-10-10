namespace DiagnosticsVisualizer.Runtime;

public sealed record CaptureOptions
{
    public int EventsPerThread { get; init; } = 4096;
    public int MaxThreads { get; init; } = 128;
    public int MaxObjects { get; init; } = 100_000;
    public int MaxSymbols { get; init; } = 65_536;
    public int SampleEvery { get; init; } = 1;
    public ulong SubsystemMask { get; init; } = ulong.MaxValue;
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
