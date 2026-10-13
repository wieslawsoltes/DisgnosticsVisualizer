using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public static partial class DemoTraceFactory
{
    public static TraceBlock[] CreateGpu()
    {
        var events = new List<TraceEvent>();
        for (var i = 0; i < 12; i++)
        {
            var id = i * 8L + 1; var cpu = i * 16667L; var context = i == 10 ? 200 : 100; var symbol = i == 10 ? 2 : 1;
            void Event(long offset, long timestamp, long code, long data = 0) => events.Add(new(id + offset, offset == 0 ? 0 : id, timestamp, 0, context, 0, 1, symbol, Subsystem.Rendering, TraceEventKind.Link, code, data));
            Event(0, cpu, GpuTimingEvent.Begin);
            if (i == 10) { Event(1, cpu + 10, GpuTimingEvent.Completed, (long)GpuQueryOutcome.Unsupported); continue; }
            Event(1, cpu + 500, GpuTimingEvent.Submitted);
            if (i == 11) { Event(4, cpu + 5000, GpuTimingEvent.Completed, (long)GpuQueryOutcome.PendingLimit); continue; }
            Event(2, cpu + 5000, GpuTimingEvent.CounterStart, 1_000_000_000 + i * 20_000_000L);
            Event(3, cpu + 5001, GpuTimingEvent.CounterEnd, 1_002_000_000 + i * 20_150_000L);
            Event(4, cpu + 5002, GpuTimingEvent.Completed, (long)GpuQueryOutcome.Available);
        }
        return [new()
        {
            SessionId = Guid.Parse("f3d3bd64-37b9-430a-b5b2-628c6afc1934"), Sequence = 1, Frequency = 1_000_000, Recording = false,
            Symbols = [new(1, GpuTimingSymbol.Encode(new("Synthetic GL submission", "Synthetic OpenGL", "Demo", "Synthetic GPU, not hardware", 64, "Illustration only; not a benchmark."))),
                new(2, GpuTimingSymbol.Encode(new("Unsupported synthetic context", "Synthetic GLES", "Demo", "Synthetic context", 0)))],
            Objects = [new(100, "GpuContext", "Synthetic desktop GL"), new(200, "GpuContext", "Synthetic unsupported context")], Events = events.ToArray()
        }];
    }
}
