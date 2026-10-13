using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class GpuTimingTests
{
    private sealed class Backend : IGpuTimestampBackend
    {
        private uint _next;
        private readonly ulong[] _values = new ulong[1024];
        public GpuTimingSite Site { get; set; } = new("GPU test fixture", "Synthetic", "Test", "Deterministic", 64);
        public bool IsLost { get; set; }
        public bool CanRead { get; set; } = true;
        public bool Ready = true, ThrowOnTimestamp, ReverseClock;
        public int Creates, Deletes, Stamps, Reads;
        public uint CreateQuery() { Creates++; return ++_next; }
        public void DeleteQuery(uint query) => Deletes++;
        public void Timestamp(uint query)
        {
            if (ThrowOnTimestamp) throw new InvalidOperationException("Driver failure fixture");
            Stamps++; _values[query] = (ulong)(query % 2 == 0 ? (ReverseClock ? 50 : 1_000_100) : 100);
        }
        public bool TryRead(uint query, out ulong value) { value = 0; Reads++; if (!Ready) return false; value = _values[query]; return true; }
    }
    private static CaptureSession Start() => Probe.Start(new() { TrackGpuTimings = true, EventsPerThread = 131072 });
    private static TraceView View(CaptureSession session)
    {
        var store = new TraceStore();
        while (true) { var b = session.Drain(); store.Add(b); if (b.Events.Length == 0) break; }
        return store.GetView();
    }
    [Fact] public void DeferredQueriesStayPendingUntilAvailabilityAndKeepCpuClockSeparate()
    {
        using var session = Start(); var backend = new Backend { Ready = false };
        using var arena = new GpuTimestampArena(backend, new object());
        var token = arena.Begin(); Assert.True(token.IsValid); token.Submit(); arena.Poll();
        Assert.Equal(1, arena.PendingCount); Assert.Equal(0, session.ProbeFaults);
        backend.Ready = true; arena.Poll();
        var trace = View(session); var row = Assert.Single(new GpuTimingIndex(trace).Rows);
        Assert.Equal("Available", row.Status); Assert.Equal(1d, row.GpuMs); Assert.Equal((ulong)100, row.DeviceStartNs);
        Assert.All(trace.Events, e => Assert.Equal(TraceEventKind.Link, e.Kind));
        Assert.Empty(TraceAnalyzer.Analyze(trace).Costs); Assert.Equal(0, arena.PendingCount);
    }
    [Fact] public void CapacityAndGenerationRejectStaleOrDuplicateSubmissions()
    {
        using var session = Start(); var backend = new Backend { Ready = false };
        using var arena = new GpuTimestampArena(backend, new object(), 1, 1);
        var old = arena.Begin(); old.Submit(); old.Submit(); Assert.False(arena.Begin().IsValid);
        backend.Ready = true; arena.Poll(); var next = arena.Begin(); old.Submit(); next.Submit(); arena.Poll();
        var rows = new GpuTimingIndex(View(session)).Rows;
        Assert.Equal(3, rows.Length); Assert.Equal(2, rows.Count(r => r.IsAvailable));
        Assert.Single(rows, r => r.Status == "CapacityExceeded"); Assert.Equal(2, backend.Creates);
    }
    [Fact] public void PausedCompletionAndDisposedSessionIsolation()
    {
        var backend = new Backend(); using var arena = new GpuTimestampArena(backend, new object());
        using (var old = Start())
        {
            var a = arena.Begin(); old.SetRecording(false); a.Submit(); arena.Poll();
            Assert.False(arena.Begin().IsValid); Assert.True(Assert.Single(new GpuTimingIndex(View(old)).Rows).IsAvailable);
        }
        GpuSubmission abandoned;
        using (var old = Start()) abandoned = arena.Begin();
        using var current = Start(); abandoned.Submit(); arena.Poll();
        Assert.Empty(View(current).Events); Assert.Equal(0, current.ProbeFaults);
    }
    [Theory]
    [InlineData("abort", "SubmissionAborted")]
    [InlineData("timeout", "PendingLimit")]
    [InlineData("lost", "ContextLost")]
    [InlineData("fault", "BackendFault")]
    [InlineData("clock", "InvalidClock")]
    [InlineData("dispose", "ContextDisposed")]
    public void FailuresNeverProduceInventedDeviceDurations(string kind, string expected)
    {
        using var session = Start(); var backend = new Backend { ThrowOnTimestamp = kind == "fault", ReverseClock = kind == "clock" };
        using var arena = new GpuTimestampArena(backend, new object(), 1, 1, 1);
        var token = arena.Begin();
        if (kind == "abort") token.Abort();
        else if (kind == "dispose") arena.Dispose();
        else
        {
            token.Submit(); backend.IsLost = kind == "lost"; backend.CanRead = kind != "timeout";
            arena.Poll(); arena.Poll();
        }
        var row = Assert.Single(new GpuTimingIndex(View(session)).Rows);
        Assert.Equal(expected, row.Status); Assert.Null(row.GpuMs); Assert.Equal(0, arena.PendingCount);
    }
    [Fact] public void UnsupportedBackendDoesNotAllocateDeviceQueries()
    {
        using var session = Start(); var backend = new Backend { Site = new("Unsupported fixture", "GLES", "", "", 0) };
        using var arena = new GpuTimestampArena(backend, new object()); Assert.False(arena.Begin().IsValid);
        Assert.Equal("Unsupported", Assert.Single(new GpuTimingIndex(View(session)).Rows).Status); Assert.Equal(0, backend.Creates);
    }
    [Fact] public void ProbeHotPathsRemainAllocationFreeAfterFixedWarmup()
    {
        var backend = new Backend(); using var arena = new GpuTimestampArena(backend, new object());
        Assert.Equal(0, AllocationMeasurement.Measure(n => { for (var i = 0; i < n; i++) arena.Begin(); }));
        Assert.Equal(0, backend.Creates);
        using var session = Start();
        Assert.Equal(0, AllocationMeasurement.Measure(n => { for (var i = 0; i < n; i++) { var t = arena.Begin(); t.Submit(); arena.Poll(); } }));
        Assert.Equal(0, session.ProbeFaults); Assert.Equal(0, session.DroppedEvents);
    }
    [Fact] public async Task MetadataCountersAndClockSemanticsSurviveBinaryAndChromeExport()
    {
        using var session = Start(); using var arena = new GpuTimestampArena(new Backend(), new object());
        var t = arena.Begin(); t.Submit(); arena.Poll(); var trace = View(session);
        using var binary = new MemoryStream(); await TraceExport.WriteBinaryAsync(binary, trace); binary.Position = 0;
        var store = new TraceStore(); await TraceFile.ReadAsync(binary, b => { store.Add(b); return ValueTask.CompletedTask; });
        Assert.Equal(new GpuTimingIndex(trace).Rows, new GpuTimingIndex(store.GetView()).Rows);
        using var chrome = new MemoryStream(); await TraceExport.WriteChromeJsonAsync(chrome, trace); chrome.Position = 0;
        using var json = await JsonDocument.ParseAsync(chrome);
        var row = json.RootElement.GetProperty("gpuQueries")[0];
        Assert.Equal("1000000", row.GetProperty("elapsedNs").GetString());
        Assert.Equal("uncalibrated device nanoseconds", row.GetProperty("clock").GetString());
        Assert.DoesNotContain(json.RootElement.GetProperty("traceEvents").EnumerateArray(), e => e.GetProperty("ph").GetString() == "X");
    }
    [Fact] public void MissingAndConflictingMarkersAreNotCompleteMeasurements()
    {
        using var session = Start(); using var arena = new GpuTimestampArena(new Backend(), new object());
        var t = arena.Begin(); t.Submit(); arena.Poll(); var view = View(session);
        foreach (var removed in view.Events)
            Assert.False(Assert.Single(new GpuTimingIndex(view with { Events = view.Events.Where(e => e.Id != removed.Id).ToArray() }).Rows).IsAvailable);
        var duplicate = view.Events.First(e => e.Data0 == GpuTimingEvent.Submitted) with { Id = 9999 };
        Assert.Equal("Invalid markers", Assert.Single(new GpuTimingIndex(view with { Events = [.. view.Events, duplicate] }).Rows).Status);
        var store = new TraceStore();
        Assert.Throws<InvalidDataException>(() => store.Add(new() { SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000, Symbols = [new(1, GpuTimingSymbol.Prefix + "[]")] }));
        Assert.Equal(Guid.Empty, store.GetView().SessionId);
    }
    [Fact] public void ConfigurationRemainsExplicitlyOptIn()
    {
        Assert.False(CaptureOptions.FromEnvironment(_ => null).TrackGpuTimings);
        Assert.True(CaptureOptions.FromEnvironment(n => n == "AVTRACE_GPU_TIMINGS" ? "1" : null).TrackGpuTimings);
    }
}
