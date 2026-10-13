using System.Diagnostics;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct GpuCaptureToken(WeakReference<CaptureSession>? Session, long Id, long ContextId, int NameId)
{
    public bool IsValid => Session is not null && Id != 0;
}

public static partial class Probe
{
    public static bool GpuEnabled => Current is { Recording: true, Options.TrackGpuTimings: true } session &&
        !_insideProbe && _suppressionDepth == 0 && (session.Options.SubsystemMask & (1UL << (int)Subsystem.Rendering)) != 0;
    internal static GpuCaptureToken GpuBegin(string descriptor, object context)
    {
        if (!GpuEnabled || Current is not { } session) return default;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return default; }
            if (++writer.Samples % session.Options.SampleEvery != 0) return default;
            var name = session.Intern(writer, descriptor); var obj = session.GetObjectId(context);
            if (name == 0 || obj == 0) return default;
            var id = writer.NextId();
            if (!writer.TryWrite(new(id, LogicalParent(session, writer), Stopwatch.GetTimestamp(), 0, obj, 0,
                writer.ThreadId, name, Subsystem.Rendering, TraceEventKind.Link, GpuTimingEvent.Begin))) session.Drop();
            return new(session.GpuWeakReference, id, obj, name);
        }
        catch { session.Fault(); return default; }
        finally { _insideProbe = false; }
    }
    internal static void GpuSubmitted(in GpuCaptureToken token) => GpuWrite(token, GpuTimingEvent.Submitted, 0);
    internal static void GpuCounters(in GpuCaptureToken token, ulong start, ulong end)
    { GpuWrite(token, GpuTimingEvent.CounterStart, unchecked((long)start)); GpuWrite(token, GpuTimingEvent.CounterEnd, unchecked((long)end)); }
    internal static void GpuComplete(in GpuCaptureToken token, GpuQueryOutcome outcome) => GpuWrite(token, GpuTimingEvent.Completed, (long)outcome);
    private static void GpuWrite(in GpuCaptureToken token, long code, long value)
    {
        if (token.Session is null || !token.Session.TryGetTarget(out var session) || !ReferenceEquals(Current, session)) return;
        if (_insideProbe) { session.Drop(); return; }
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return; }
            if (!writer.TryWrite(new(writer.NextId(), token.Id, Stopwatch.GetTimestamp(), 0, token.ContextId, 0,
                writer.ThreadId, token.NameId, Subsystem.Rendering, TraceEventKind.Link, code, value))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
}
