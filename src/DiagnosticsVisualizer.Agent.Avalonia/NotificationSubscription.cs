using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;

namespace DiagnosticsVisualizer.Agent.Avalonia;

/// <summary>Subscriptions do not root source objects or the inspector. Disposal is deterministic when possible.</summary>
internal sealed class NotificationSubscription : IDisposable
{
    private readonly WeakReference<object> _source;
    private readonly WeakReference<AvaloniaInspector> _inspector;
    private bool _disposed;
    public NotificationSubscription(object source, AvaloniaInspector inspector)
    {
        _source = new(source); _inspector = new(inspector);
        if (source is AvaloniaObject avalonia) avalonia.PropertyChanged += AvaloniaChanged;
        else if (source is INotifyPropertyChanged inpc) inpc.PropertyChanged += PropertyChanged;
        if (source is INotifyCollectionChanged incc) incc.CollectionChanged += CollectionChanged;
    }
    private void AvaloniaChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (!_inspector.TryGetTarget(out var inspector) || inspector.IsDisposed) { Dispose(); return; }
        Probe.Instant(Subsystem.Properties, args.Property.Name, sender, args.Property);
        if (args.Property.Name is "DataContext" or "ItemsSource" && args.NewValue is { } value) inspector.Observe(value);
    }
    private void PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_inspector.TryGetTarget(out var inspector) || inspector.IsDisposed) { Dispose(); return; }
        Probe.PropertyNotification(sender, args);
    }
    private void CollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_inspector.TryGetTarget(out var inspector) || inspector.IsDisposed) { Dispose(); return; }
        Probe.CollectionNotification(sender, args);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (!_source.TryGetTarget(out var source)) return;
        if (source is AvaloniaObject avalonia) avalonia.PropertyChanged -= AvaloniaChanged;
        else if (source is INotifyPropertyChanged inpc) inpc.PropertyChanged -= PropertyChanged;
        if (source is INotifyCollectionChanged incc) incc.CollectionChanged -= CollectionChanged;
    }
}
