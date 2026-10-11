using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Media;
using DiagnosticsVisualizer.Charts;
using Dock.Model.Core;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private readonly Dictionary<string, Button> _panelButtons = new(StringComparer.Ordinal);
    private readonly List<(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler)> _railObservers = [];
    public bool IsFocusMode { get; private set; }
    public void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;
        // Do not mutate docking topology or saved presets just to recover vertical space.
        foreach (var child in _root.Children.Where(c => Grid.GetRow(c) == 1 && Grid.GetRowSpan(c) == 1))
            child.IsVisible = !IsFocusMode;
        _root.RowDefinitions[1].Height = new(IsFocusMode ? 0 : 44);
        _overview.IsVisible = !IsFocusMode;
    }
    private void BindPanelRails()
    {
        UnbindPanelRails();
        foreach (var (id, button) in _panelButtons)
        {
            if (!_factory.Panels.TryGetValue(id, out var panel) || panel is not IDockingWindowState state ||
                panel is not INotifyPropertyChanged changed) continue;
            void Update()
            {
                var selected = state.IsSelected || state.IsActive;
                button.Background = selected ? ChartPalette.Selection : Brushes.Transparent;
                if (button.Content is Path icon) icon.Stroke = selected ? ChartPalette.Foreground : ChartPalette.Muted;
            }
            PropertyChangedEventHandler handler = (_, e) =>
            { if (e.PropertyName is nameof(IDockingWindowState.IsSelected) or nameof(IDockingWindowState.IsActive) or null or "") Update(); };
            changed.PropertyChanged += handler; _railObservers.Add((changed, handler)); Update();
        }
    }
    private void UnbindPanelRails()
    {
        foreach (var (source, handler) in _railObservers) source.PropertyChanged -= handler;
        _railObservers.Clear();
    }
}
