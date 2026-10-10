using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace DiagnosticsVisualizer.Controls;

/// <summary>
/// Hosts a long-lived tool view across docking/template recreation. Only one attached presenter
/// owns the view at a time; an old cached presenter releases its child synchronously before transfer.
/// The weak owner registry cannot retain detached windows. Access is confined to the UI thread.
/// </summary>
public sealed class ControlLeaseHost(Control view) : ContentControl
{
    private sealed class Lease { public WeakReference<ControlLeaseHost>? Owner; }
    private static readonly ConditionalWeakTable<Control, Lease> Owners = new();
    protected override Type StyleKeyOverride => typeof(ContentControl);
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var lease = Owners.GetOrCreateValue(view);
        if (lease.Owner?.TryGetTarget(out var previous) == true && !ReferenceEquals(previous, this)) previous.Release();
        lease.Owner = new(this); Content = view;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Release(); base.OnDetachedFromVisualTree(e);
    }
    private void Release()
    {
        if (!ReferenceEquals(Content, view)) return;
        Content = null; Presenter?.UpdateChild();
        if (Owners.TryGetValue(view, out var lease) && lease.Owner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this)) lease.Owner = null;
    }
}
