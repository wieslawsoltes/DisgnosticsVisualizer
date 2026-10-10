using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class CaptureEnvironmentTests
{
    [Fact]
    public void StartupSettingsAreParsedWithoutMutatingProcessEnvironment()
    {
        var values = new Dictionary<string, string>
        {
            ["AVTRACE_EVENTS_PER_THREAD"] = "65536", ["AVTRACE_MAX_THREADS"] = "8",
            ["AVTRACE_SAMPLE_EVERY"] = "4", ["AVTRACE_SUBSYSTEM_MASK"] = "0xFF"
        };
        var options = CaptureOptions.FromEnvironment(name => values.GetValueOrDefault(name));
        Assert.Equal(65536, options.EventsPerThread); Assert.Equal(8, options.MaxThreads);
        Assert.Equal(4, options.SampleEvery); Assert.Equal(255UL, options.SubsystemMask);
        Assert.Equal(new CaptureOptions(), CaptureOptions.FromEnvironment(_ => null));
    }
    [Theory]
    [InlineData("AVTRACE_EVENTS_PER_THREAD", "65535")]
    [InlineData("AVTRACE_EVENTS_PER_THREAD", "2097152")]
    [InlineData("AVTRACE_MAX_THREADS", "0")]
    [InlineData("AVTRACE_SAMPLE_EVERY", "0")]
    [InlineData("AVTRACE_SUBSYSTEM_MASK", "0xZZ")]
    [InlineData("AVTRACE_EVENTS_PER_THREAD", "99999999999999999999")]
    public void InvalidBudgetsAreRejectedBeforeRecordingStarts(string key, string value)
        => Assert.ThrowsAny<ArgumentException>(() => CaptureOptions.FromEnvironment(name => name == key ? value : null));
}
