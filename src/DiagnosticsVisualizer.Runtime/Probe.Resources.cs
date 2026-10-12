using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

internal readonly record struct ResourceContext(CaptureSession? Session, CaptureWriter? Writer, long Id);
internal sealed record ResourceLabel(string Text);

public static partial class Probe
{
    [ThreadStatic] private static ResourceContext _resourceContext;
    [ThreadStatic] private static int _resourceDepth;
    private static readonly ConditionalWeakTable<object, ResourceLabel>.CreateValueCallback CreateResourceLabel = value => new(SafeResourceLabel(value));

    /// <summary>Never evaluates a resource or invokes a key's Equals/GetHashCode/ToString.</summary>
    public static ResourceLookupScope BeginResourceLookup(string site, object? provider, object? key, object? themeKey)
    {
        var previous = _resourceContext; _resourceContext = default;
        var scope = new ResourceLookupScope(previous, ++_resourceDepth, Environment.CurrentManagedThreadId);
        var session = Current;
        if (session?.Options.TrackResourceLookups != true)
        { scope.Fallback = Enter((int)Subsystem.Resources, site, provider, key); return scope; }
        if (_insideProbe || _suppressionDepth != 0 ||
            (session.Options.SubsystemMask & (1UL << (int)Subsystem.Resources)) == 0 ||
            (_resourceDepth > 1 ? !ReferenceEquals(previous.Session, session) : !session.Recording)) return scope;
        _insideProbe = true;
        try
        {
            var writer = session.Writer;
            if (writer is null) { session.Drop(); return scope; }
            // Sample whole lookup trees, not individual levels. Disabled nested calls shadow their parent.
            if (_resourceDepth == 1 && ++writer.Samples % session.Options.SampleEvery != 0) return scope;
            var providerId = session.GetObjectId(provider); var keyId = session.GetObjectId(key); var themeId = session.GetObjectId(themeKey);
            var keyName = session.Intern(writer, key is null ? "key: <null>" : keyId == 0 ? "key: <metadata omitted>" : session.ResourceLabels.GetValue(key, CreateResourceLabel).Text);
            var themeName = session.Intern(writer, themeKey is null ? "theme: <unspecified>" : themeId == 0 ? "theme: <metadata omitted>" : session.ResourceLabels.GetValue(themeKey, CreateResourceLabel).Text);
            var siteName = session.Intern(writer, site);
            var restoreParent = writer.Parent; var parent = LogicalParent(session, writer); var id = writer.NextId();
            var flow = session.Options.FlowExecutionContext ? Ambient.Value : default;
            if (session.Options.FlowExecutionContext) Ambient.Value = new(session.Id, id);
            writer.Parent = id; _resourceContext = new(session, writer, id);
            scope.Initialize(session, writer, id, parent, siteName, providerId, restoreParent, flow);
            var start = scope.Start;
            if (!writer.TryWrite(new(writer.NextId(), id, start, 0, keyId, providerId, writer.ThreadId, keyName,
                Subsystem.Resources, TraceEventKind.Link, ResourceLookupEvent.RequestCode, previous.Id))) session.Drop();
            if (!writer.TryWrite(new(writer.NextId(), id, start, 0, themeId, 0, writer.ThreadId, themeName,
                Subsystem.Resources, TraceEventKind.Link, ResourceLookupEvent.ThemeCode, previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
        return scope;
    }
    internal static void ResourceReturned(ref ResourceLookupScope scope, bool found, object? value)
    {
        if (scope.Session is not { } session) return;
        scope.Outcome = found ? value is null ? ResourceLookupOutcome.FoundNull : ResourceLookupOutcome.Found : ResourceLookupOutcome.Missing;
        if (!found || value is null || _insideProbe) return;
        _insideProbe = true;
        try { scope.ValueId = session.GetObjectId(value); }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    internal static void EndResource(ref ResourceLookupScope scope)
    {
        if (scope.Depth == 0) return;
        if (scope.Thread != Environment.CurrentManagedThreadId || _resourceDepth != scope.Depth)
        { Current?.Fault(); return; }
        _resourceDepth--; _resourceContext = scope.Previous; scope.Depth = 0;
        if (scope.Session is not { } session || scope.Writer is not { } writer) { scope.Fallback.Dispose(); return; }
        if (_insideProbe || writer.Parent != scope.Id) { session.Fault(); return; }
        _insideProbe = true;
        try
        {
            if (session.Options.FlowExecutionContext && Ambient.Value.SessionId == session.Id) Ambient.Value = scope.PreviousFlow;
            writer.Parent = scope.RestoreParent;
            if (ReferenceEquals(Current, session) && !writer.TryWrite(new(scope.Id, scope.Parent, scope.Start,
                Math.Max(0, Stopwatch.GetTimestamp() - scope.Start), scope.ProviderId, scope.ValueId, writer.ThreadId, scope.NameId,
                Subsystem.Resources, TraceEventKind.Span, ResourceLookupEvent.CompleteCode(scope.Outcome), scope.Previous.Id))) session.Drop();
        }
        catch { session.Fault(); }
        finally { _insideProbe = false; }
    }
    private static string SafeResourceLabel(object value)
    {
        // Only immutable built-in values receive a printable label. Custom key methods are never called.
        var text = value switch
        {
            string s => s.Length > 512 ? s[..512] + "… [truncated]" : s,
            Type t when t.GetType() == typeof(object).GetType() => t.FullName ?? t.Name,
            int n => n.ToString(CultureInfo.InvariantCulture), long n => n.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false", Guid g => g.ToString("D"),
            _ => "<" + (value.GetType().FullName ?? value.GetType().Name) + "> [opaque key]"
        };
        if (text.Length > 512) text = text[..512] + "… [truncated]";
        // Prefix also prevents a user-supplied key from masquerading as reserved trace metadata.
        return "key: " + text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
    }
}

/// <summary>Synchronous, thread-affine lookup lease. Dispose also observes exceptional unwinding.</summary>
public ref struct ResourceLookupScope
{
    internal readonly ResourceContext Previous;
    internal int Depth;
    internal readonly int Thread;
    internal TraceScope Fallback;
    internal CaptureSession? Session;
    internal CaptureWriter? Writer;
    internal long Id, Parent, Start, ProviderId, ValueId, RestoreParent;
    internal int NameId;
    internal TraceContext PreviousFlow;
    internal ResourceLookupOutcome Outcome;
    internal ResourceLookupScope(ResourceContext previous, int depth, int thread)
    { this = default; Previous = previous; Depth = depth; Thread = thread; }
    internal void Initialize(CaptureSession session, CaptureWriter writer, long id, long parent, int name, long provider, long restore, TraceContext flow)
    { Session = session; Writer = writer; Id = id; Parent = parent; NameId = name; ProviderId = provider; RestoreParent = restore; PreviousFlow = flow; Start = Stopwatch.GetTimestamp(); }
    public void Returned(bool found, object? value) => Probe.ResourceReturned(ref this, found, value);
    public void Dispose() => Probe.EndResource(ref this);
}
