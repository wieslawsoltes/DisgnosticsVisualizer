using System.ComponentModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>Weakly-consistent tree checkpoints captured in bounded dispatcher slices, never an application freeze.</summary>
public sealed class AvaloniaInspector : IDisposable
{
    private sealed class SnapshotWork(long selected)
    {
        public readonly long Selected = selected;
        public readonly Stack<object> Pending = new();
        public readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);
        public readonly List<NodeSnapshot> Nodes = [];
        public readonly List<RelationSnapshot> Relations = [];
        public bool Truncated;
    }
    private readonly CaptureSession _session;
    private readonly AgentOptions _options;
    private readonly ValueRedactor _redactor;
    private readonly ConditionalWeakTable<object, NotificationSubscription> _observed = new();
    private readonly List<WeakReference<NotificationSubscription>> _subscriptions = [];
    private readonly List<WeakReference<Visual>> _roots = [];
    private readonly ConditionalWeakTable<object, object> _excludedRoots = new();
    private readonly object _subscriptionGate = new();
    private int _observedCount, _pending, _disposed;
    private long _selected;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public event Action<string>? InspectionError;
    public AvaloniaInspector(CaptureSession session, AgentOptions? options = null)
    { _session = session; _options = options ?? new(); _redactor = new(_options); }
    public void AttachRoot(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root); lock (_roots) _roots.Add(new(root)); RequestSnapshot();
    }
    public void ExcludeRoot(Visual root)
    { ArgumentNullException.ThrowIfNull(root); _excludedRoots.GetValue(root, static _ => new()); }
    public void Observe(object source)
    {
        if (IsDisposed || !_options.ObserveNotifications || source is not (AvaloniaObject or INotifyPropertyChanged or INotifyCollectionChanged)) return;
        lock (_subscriptionGate)
        {
            if (_observedCount >= _options.MaxObservedObjects || _observed.TryGetValue(source, out _)) return;
            var subscription = new NotificationSubscription(source, this); _observed.Add(source, subscription);
            _subscriptions.Add(new(subscription)); _observedCount++;
        }
    }
    public void RequestSnapshot(long selectedObject = 0)
    {
        if (selectedObject != 0) Interlocked.Exchange(ref _selected, selectedObject);
        if (IsDisposed || Interlocked.Exchange(ref _pending, 1) != 0) return;
        Dispatcher.UIThread.Post(BeginSnapshot, DispatcherPriority.Background);
    }
    private void BeginSnapshot()
    {
        if (IsDisposed) { Volatile.Write(ref _pending, 0); return; }
        try
        {
            var work = new SnapshotWork(Interlocked.Read(ref _selected));
            lock (_roots)
                for (var i = _roots.Count - 1; i >= 0; i--)
                    if (_roots[i].TryGetTarget(out var root)) work.Pending.Push(root); else _roots.RemoveAt(i);
            switch (Application.Current?.ApplicationLifetime)
            {
                case IClassicDesktopStyleApplicationLifetime desktop:
                    foreach (var window in desktop.Windows) work.Pending.Push(window); break;
                case ISingleViewApplicationLifetime { MainView: { } view }: work.Pending.Push(view); break;
            }
            ProcessSlice(work);
        }
        catch (Exception error) { Report(error); Volatile.Write(ref _pending, 0); }
    }
    private void ProcessSlice(SnapshotWork work)
    {
        if (IsDisposed) { Volatile.Write(ref _pending, 0); return; }
        var start = Stopwatch.GetTimestamp(); var count = 0;
        try
        {
            while (work.Pending.Count > 0 && count++ < 128 && Stopwatch.GetElapsedTime(start) < _options.SnapshotSliceBudget)
            {
                var value = work.Pending.Pop();
                if (!work.Seen.Add(value) || _excludedRoots.TryGetValue(value, out _)) continue;
                if (work.Nodes.Count >= TraceLimits.MaxNodesPerSnapshot) { work.Truncated = true; work.Pending.Clear(); break; }
                var id = _session.GetObjectId(value);
                if (id == 0) { work.Truncated = true; continue; }
                var visual = value as Visual; var logical = value as ILogical; var styled = value as StyledElement;
                var bounds = visual?.Bounds ?? default;
                work.Nodes.Add(new(id, _session.GetObjectId(visual?.GetVisualParent()), _session.GetObjectId(logical?.LogicalParent),
                    _session.GetObjectId(styled?.TemplatedParent), value.GetType().FullName ?? value.GetType().Name,
                    ValueRedactor.Limit(styled?.Name ?? "", 256), bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    visual?.IsVisible ?? true, styled is null ? "" : ValueRedactor.Limit(string.Join(" ", styled.Classes), 1024)));
                Observe(value);
                if (styled?.DataContext is { } context)
                {
                    Observe(context); work.Relations.Add(new(id, _session.GetObjectId(context), "DataContext", context.GetType().FullName ?? context.GetType().Name));
                }
                if (value is ItemsControl items)
                {
                    Observe(items.Items);
                    if (items.ItemsSource is { } source) { Observe(source); work.Relations.Add(new(id, _session.GetObjectId(source), "ItemsSource", source.GetType().Name)); }
                }
                if (visual is not null) foreach (var child in visual.GetVisualChildren()) work.Pending.Push(child);
                if (logical is not null) foreach (var child in logical.LogicalChildren) work.Pending.Push(child);
            }
            if (work.Pending.Count > 0)
            { Dispatcher.UIThread.Post(() => ProcessSlice(work), DispatcherPriority.Background); return; }
            var selected = work.Selected != 0 ? work.Selected : work.Nodes.FirstOrDefault()?.Id ?? 0;
            var properties = InspectProperties(selected, work.Relations);
            var relations = work.Relations.Take(TraceLimits.MaxRelationsPerSnapshot).ToArray();
            _session.SetSnapshot(new(work.Nodes.ToArray(), properties, relations, selected,
                work.Truncated || relations.Length != work.Relations.Count));
        }
        catch (Exception error) { work.Pending.Clear(); Report(error); }
        finally { if (work.Pending.Count == 0) Volatile.Write(ref _pending, 0); }
    }
    public PropertySnapshot[] InspectProperties(long id, List<RelationSnapshot>? relations = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_session.ResolveObject(id) is not AvaloniaObject value) return [];
        var properties = new List<PropertySnapshot>(); var registry = AvaloniaPropertyRegistry.Instance;
        foreach (var property in registry.GetRegistered(value.GetType()).Concat(registry.GetRegisteredAttached(value.GetType())).Distinct().Take(2048))
        {
            try
            {
                var diagnostic = value.GetDiagnostic(property);
                properties.Add(new(id, property.Name, property.IsDirect ? "Direct" : property.IsAttached ? "Attached" : "Styled",
                    _redactor.Format(property.Name, diagnostic.Value), diagnostic.Priority.ToString(),
                    ValueRedactor.Limit(diagnostic.Diagnostic ?? (diagnostic.IsOverriddenCurrentValue ? "SetCurrentValue" : property.OwnerType.Name))));
            }
            catch (Exception error) { properties.Add(new(id, property.Name, "Unavailable", "", "", error.GetType().Name)); }
        }
        try
        {
            foreach (var frame in value.GetValueStoreDiagnostic().GetAppliedFrames())
            {
                var source = frame.Source switch
                {
                    Style style => style.Selector?.ToString() ?? "Style",
                    ControlTheme theme => $"ControlTheme {theme.TargetType?.Name}",
                    null => frame.Type,
                    _ => frame.Source.GetType().Name
                };
                foreach (var entry in frame.Values)
                {
                    if (properties.Count >= TraceLimits.MaxPropertiesPerSnapshot) break;
                    properties.Add(new(id, entry.Property.Name, $"{frame.Type} frame · {(frame.IsActive ? "active" : "inactive")}",
                        _redactor.Format(entry.Property.Name, entry.Value), frame.Priority, ValueRedactor.Limit(source)));
                }
                if (frame.Source is { } frameSource) relations?.Add(new(id, _session.GetObjectId(frameSource), "Value frame", ValueRedactor.Limit(source)));
            }
        }
        catch (Exception error) { properties.Add(new(id, "Style provenance", "Capability", "Unavailable", "", error.GetType().Name)); }
        if (value is StyledElement styled)
        {
            foreach (var resource in styled.Resources.Take(256))
            {
                var key = resource.Key is string text ? ValueRedactor.Limit(text, 256) : $"<{resource.Key.GetType().Name}>";
                properties.Add(new(id, key, "Local resource", _redactor.Format(key, resource.Value), "Resource dictionary", styled.GetType().Name));
                if (resource.Value is { } resourceValue) relations?.Add(new(id, _session.GetObjectId(resourceValue), "Resource", key));
            }
            foreach (var style in styled.Styles.Take(128))
                relations?.Add(new(id, _session.GetObjectId(style), "Style", style is Style s ? ValueRedactor.Limit(s.Selector?.ToString() ?? "Style") : style.GetType().Name));
        }
        if (value is Control control)
        {
            if (control.Theme is { } theme) relations?.Add(new(id, _session.GetObjectId(theme), "ControlTheme", theme.TargetType?.Name ?? ""));
            foreach (var template in control.DataTemplates.Take(128)) relations?.Add(new(id, _session.GetObjectId(template), "DataTemplate", template.GetType().Name));
        }
        if (value is TemplatedControl { Template: { } templateValue }) relations?.Add(new(id, _session.GetObjectId(templateValue), "ControlTemplate", templateValue.GetType().Name));
        return properties.Take(TraceLimits.MaxPropertiesPerSnapshot).ToArray();
    }
    private void Report(Exception error)
    {
        try { InspectionError?.Invoke($"{error.GetType().Name}: inspection capability unavailable"); } catch { }
        Probe.Instant(Subsystem.Diagnostics, "InspectionFault", null, null, 1);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        void Unsubscribe()
        {
            lock (_subscriptionGate)
            {
                foreach (var weak in _subscriptions) if (weak.TryGetTarget(out var subscription)) subscription.Dispose();
                _subscriptions.Clear(); _observed.Clear();
            }
            lock (_roots) _roots.Clear();
        }
        if (Dispatcher.UIThread.CheckAccess()) Unsubscribe(); else Dispatcher.UIThread.Post(Unsubscribe, DispatcherPriority.Background);
    }
}
