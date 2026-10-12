using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A click on passive chrome must activate this workbench's keyboard route. Do not
        // take focus from an editor/grid that already handled the click or owns focus here,
        // and never grab an embedding application's focus merely because we were mounted.
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is not Visual visual || !ReferenceEquals(visual, this) && !this.IsVisualAncestorOf(visual))
            Focus();
    }
}
