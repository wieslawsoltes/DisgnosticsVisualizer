using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public static partial class DemoTraceFactory
{
    /// <summary>Explicit synthetic native recording. Never mixed into a managed or real target session.</summary>
    public static TraceBlock[] CreateNative()
    {
        var session = Guid.Parse("6536924b-5c72-41f7-842a-d889a4b58d3b"); const long origin = 1_000_000_000;
        var template = new TraceBlock { SessionId = session, Frequency = 1_000_000_000, OriginTimestamp = origin, UtcStartTicks = 638_000_000_000_000_000, Recording = false };
        var sites = new[] { "Synthetic.App::Layout", "Synthetic.App::Render", "Synthetic.App::Load" }.Select((name, n) => new Symbol(n + 1,
            SourceSiteSymbol.Encode(new("SyntheticNative", session, 0x06000001 + n, name, "", 0, 0, 0, 0, "", "", "", false)))).ToArray();
        var events = new List<TraceEvent>();
        for (var n = 0; n < 32; n++)
        {
            var slot = n % 2 + 1; var cookie = ((long)slot << 48) | (uint)(n + 1); var timestamp = origin + n * 2_000_000L;
            events.Add(new(cookie * 2, 0, timestamp, 0, 0, 0, slot, n % 3 + 1, Subsystem.Application, TraceEventKind.Link, NativeMethodEvent.BeginCode, cookie));
            events.Add(new(cookie * 2 + 1, 0, timestamp, 600_000 + n * 10_000, 0, 0, slot, n % 3 + 1, Subsystem.Application, TraceEventKind.Span,
                n % 7 == 0 ? NativeMethodEvent.UnwoundCode : NativeMethodEvent.ReturnedCode, cookie));
        }
        Symbol Meta(int id, NativeMetadata m) => new(id, NativeMetadataSymbol.Encode(m));
        return [template with { Sequence = 1, Symbols = [Meta(100, new NativeProcessInfo(123, 8)), Meta(101, new NativeThreadInfo(1, 1001)), Meta(102, new NativeThreadInfo(2, 1002))] },
            template with { Sequence = 2, Symbols = sites, Events = events.ToArray() },
            template with { Sequence = 3, Symbols = [Meta(103, new NativeMethodInfo(1, 2, 0, 1)), Meta(104, new NativeMethodInfo(2, 2, 0, 1)), Meta(105, new NativeMethodInfo(3, 2, 0, 1)),
                Meta(106, new NativeCaptureEnd(true, false, 0, origin + 100_000_000, 0, 0, 0, 3, 3, 3, 0, 0, 64, 4096))] }];
    }
}
