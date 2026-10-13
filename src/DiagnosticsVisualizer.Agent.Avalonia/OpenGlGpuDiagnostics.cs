using System.Runtime.CompilerServices;
using Avalonia.OpenGL;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>Fail-contained ABI for the checked Skia GL session adapter. No GL calls for disabled new captures.</summary>
public static class OpenGlGpuDiagnostics
{
    private sealed class State(IGlContext context)
    {
        public readonly int Thread = Environment.CurrentManagedThreadId;
        public readonly GpuTimestampArena Arena = new(new OpenGlTimestampBackend(context.GlInterface.GetProcAddress, () => context.IsLost), context);
    }
    private static readonly ConditionalWeakTable<IGlContext, State> Contexts = new();
    private static readonly WeakReference<State>?[] Budget = new WeakReference<State>[64];
    private static readonly object Gate = new();
    public static GpuSubmission Begin(object? context)
    {
        if (context is not IGlContext gl) return default;
        try
        {
            // Existing queries still poll during pause, without admitting new work.
            if (Contexts.TryGetValue(gl, out var known)) return known.Arena.Begin();
            if (!Probe.GpuEnabled) return default;
            lock (Gate)
            {
                if (Contexts.TryGetValue(gl, out known)) return known.Arena.Begin();
                var slot = Array.FindIndex(Budget, weak => weak is null || !weak.TryGetTarget(out _));
                if (slot < 0) { Probe.Instant(Subsystem.Diagnostics, "GPU context budget exhausted"); return default; }
                known = new(gl); Contexts.Add(gl, known); Budget[slot] = new(known);
            }
            return known.Arena.Begin();
        }
        catch { return default; } // Original drawing/exception behavior must not depend on instrumentation.
    }
    public static void Submit(ref GpuSubmission submission)
    { var value = submission; submission = default; try { value.Submit(); } catch { } }
    public static void Abort(ref GpuSubmission submission)
    { var value = submission; submission = default; try { value.Abort(); } catch { } }
    public static void Release(object? context)
    {
        if (context is not IGlContext gl || !Contexts.TryGetValue(gl, out var state)) return;
        // Do not migrate GL resources between threads in a diagnostic cleanup callback.
        if (state.Thread != Environment.CurrentManagedThreadId) return;
        try
        {
            if (gl.IsLost) state.Arena.Abandon(GpuQueryOutcome.ContextLost);
            else using (gl.EnsureCurrent()) state.Arena.Dispose();
        }
        catch { state.Arena.Abandon(GpuQueryOutcome.ContextLost); }
        finally { Contexts.Remove(gl); }
    }
}
