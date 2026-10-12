using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DiagnosticsVisualizer.Charts;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private readonly Button _navigateBack = NavigationButton("Navigate back (Alt+Left)", "M10 2L4 8L10 14");
    private readonly Button _navigateForward = NavigationButton("Navigate forward (Alt+Right)", "M6 2L12 8L6 14");
    private static Button NavigationButton(string title, string geometry)
    {
        var button = new Button { Width = 27, Height = 26, MinHeight = 0, Padding = new(7, 5), Background = Brushes.Transparent,
            Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = ChartPalette.Foreground, StrokeThickness = 1.4, Stretch = Stretch.Uniform } };
        ToolTip.SetTip(button, title); AutomationProperties.SetName(button, title); return button;
    }
    private Control NavigationButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _navigateBack.Click += (_, _) => _model.NavigateBack(); _navigateForward.Click += (_, _) => _model.NavigateForward();
        panel.Children.Add(_navigateBack); panel.Children.Add(_navigateForward);
        _commands.Add(("Navigate back", "Alt+Left", () => { _model.NavigateBack(); return Task.CompletedTask; }));
        _commands.Add(("Navigate forward", "Alt+Right", () => { _model.NavigateForward(); return Task.CompletedTask; }));
        return panel;
    }
}
