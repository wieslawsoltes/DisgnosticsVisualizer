using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Charts;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private Control BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(16, 0) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Border { Width = 28, Height = 28, CornerRadius = new(7), Background = new SolidColorBrush(Color.Parse("#35537A")), Child =
            new TextBlock { Text = "D", FontWeight = FontWeight.Bold, FontSize = 18, Foreground = ChartPalette.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        brand.Children.Add(logo); brand.Children.Add(new TextBlock { Text = "Diagnostics Visualizer", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(1), CornerRadius = new(4), Padding = new(6, 3), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "AVALONIA", FontSize = 9, Foreground = ChartPalette.Muted } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(ToolbarButton("Open", "M1 4H6L8 6H15V14H1ZM1 4V2H6L8 4H13V6", () => RunAsync(() => OpenAsync(false))));
        tools.Children.Add(ToolbarButton("Save", "M2 1H12L15 4V15H1V1ZM4 1V6H11V1M4 15V9H12V15", () => RunAsync(() => SaveAsync(false))));
        tools.Children.Add(new Border { Width = 1, Height = 22, Background = ChartPalette.Grid.Brush, Margin = new(5, 0) });
        tools.Children.Add(_record); tools.Children.Add(_connect); tools.Children.Add(_presetPicker);
        tools.Children.Add(ToolbarButton("Commands", "M2 4H14M2 8H14M2 12H14", () => { ShowCommands(); return Task.CompletedTask; }));
        grid.Children.Add(brand); Grid.SetColumn(tools, 2); grid.Children.Add(tools);
        return new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 0, 0, 1), Child = grid };
    }
    private Control BuildMetrics()
    {
        var grid = new Grid { ColumnDefinitions = new("2.4*,*,*,*,*,*"), Margin = new(14, 5) };
        var sourcePanel = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sourcePanel.Children.Add(new TextBlock { Text = "CAPTURE WORKSPACE", FontSize = 9, Foreground = ChartPalette.Accent }); sourcePanel.Children.Add(_source);
        grid.Children.Add(sourcePanel);
        var items = new[] { ("RETAINED EVENTS", _count), ("TRACE WINDOW", _duration), ("THREADS", _threadCount), ("TREE NODES", _nodeCount), ("LOST EVENTS", _loss) };
        for (var i = 0; i < items.Length; i++)
        {
            var stack = new StackPanel { Spacing = 3, Margin = new(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = items[i].Item1, FontSize = 9, Foreground = ChartPalette.Muted }); stack.Children.Add(items[i].Item2);
            var cell = new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(1, 0, 0, 0), Child = stack }; Grid.SetColumn(cell, i + 1); grid.Children.Add(cell);
        }
        return new Border { Background = new SolidColorBrush(Color.Parse("#2B2D30")), BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 0, 0, 1), Child = grid };
    }
    private Border BuildErrorBanner()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(14, 8) }; grid.Children.Add(_error);
        var dismiss = new Button { Content = "Dismiss", FontSize = 10, Padding = new(8, 3), Background = Brushes.Transparent };
        dismiss.Click += (_, _) => _model.ClearError(); Grid.SetColumn(dismiss, 1); grid.Children.Add(dismiss);
        return new Border { IsVisible = false, Background = new SolidColorBrush(Color.Parse("#48252E")), Child = grid };
    }
    private Control BuildFooter()
    {
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(12, 4) }; grid.Children.Add(_status);
        var shortcuts = new TextBlock { Text = "500k rolling event cap  ·  Ctrl+Shift+P commands", FontSize = 10, Foreground = ChartPalette.Muted };
        Grid.SetColumn(shortcuts, 1); grid.Children.Add(shortcuts);
        return new Border { BorderBrush = ChartPalette.Grid.Brush, BorderThickness = new(0, 1, 0, 0), Child = grid };
    }
    private Control ChartPanel(Control chart, Action fit)
    {
        var grid = new Grid { RowDefinitions = new("35,*") };
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(10, 3) };
        toolbar.Children.Add(new TextBlock { Text = "Click to inspect · drag to pan · Ctrl+wheel to zoom", FontSize = 10, Foreground = ChartPalette.Muted, VerticalAlignment = VerticalAlignment.Center });
        var fitButton = new Button { Content = "Fit", Padding = new(12, 3), FontSize = 10, MinHeight = 25 }; fitButton.Click += (_, _) => fit();
        Grid.SetColumn(fitButton, 1); toolbar.Children.Add(fitButton); grid.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = chart is TimelineControl ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll); return grid;
    }
    private Control TreePanel(TreeExplorer tree)
    {
        var grid = new Grid { RowDefinitions = new("31,*") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(8, 2), Spacing = 4 };
        var expand = new Button { Content = "Expand", Padding = new(8, 3), FontSize = 10 }; expand.Click += (_, _) => tree.ExpandAll();
        var collapse = new Button { Content = "Collapse", Padding = new(8, 3), FontSize = 10 }; collapse.Click += (_, _) => tree.CollapseAll();
        var refresh = new Button { Content = "Snapshot", Padding = new(8, 3), FontSize = 10 }; refresh.Click += async (_, _) => await RunAsync(_model.RequestSnapshotAsync);
        actions.Children.Add(expand); actions.Children.Add(collapse); actions.Children.Add(refresh); grid.Children.Add(actions); Grid.SetRow(tree, 1); grid.Children.Add(tree); return grid;
    }
    private Control EventPanel()
    {
        var grid = new Grid { RowDefinitions = new("40,Auto,*") };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(8, 4) };
        toolbar.Children.Add(_filter); toolbar.Children.Add(_category); toolbar.Children.Add(_objectOnly); grid.Children.Add(toolbar);
        Grid.SetRow(_queryError, 1); grid.Children.Add(_queryError);
        Grid.SetRow(_events, 2); grid.Children.Add(_events); return grid;
    }
    private Control CostPanel()
    {
        var grid = new Grid { ColumnDefinitions = new("2*,3*") }; grid.Children.Add(_costChart); Grid.SetColumn(_costs, 1); grid.Children.Add(_costs); return grid;
    }
    private Control ComparisonPanel()
    {
        var grid = new Grid { RowDefinitions = new("Auto,*") };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(14), Spacing = 12 };
        var load = new Button { Content = "Open baseline trace", FontSize = 11 }; load.Click += async (_, _) => await RunAsync(() => OpenAsync(true)); row.Children.Add(load);
        row.Children.Add(new TextBlock { Text = "Compare recorded self time. Use equivalent workloads and capture profiles.", FontSize = 11, Foreground = ChartPalette.Muted, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(row); Grid.SetRow(_comparison, 1); grid.Children.Add(_comparison); return grid;
    }
    private static TextBlock MetricValue() => new() { Text = "0", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = ChartPalette.Foreground };
    private static Button ToolbarButton(string label, string geometry, Func<Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new global::Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(geometry), Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Stroke = ChartPalette.Muted, StrokeThickness = 1.3, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content }; button.Classes.Add("dv-toolbar"); AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
        button.Click += async (_, _) => await action(); return button;
    }
}
