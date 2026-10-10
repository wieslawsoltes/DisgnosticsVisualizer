using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DiagnosticsVisualizer.Sample;

// This application deliberately contains no calls to the diagnostics API.
// The Build package injects bootstrap and probes into the application output.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<SampleApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
        .StartWithClassicDesktopLifetime(args);
}

public sealed class SampleApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new WorkloadWindow();
            desktop.MainWindow = window;
            window.Opened += (_, _) =>
            {
                DispatcherTimer.RunOnce(window.Exercise, TimeSpan.FromMilliseconds(150));
                // Leave the dispatcher running so sliced tree inspection and trace flushing execute.
                DispatcherTimer.RunOnce(() => desktop.Shutdown(0), TimeSpan.FromSeconds(2));
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class WorkloadWindow : Window
{
    private readonly WorkloadModel _model = new();
    public WorkloadWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
    }
    public void Exercise()
    {
        var button = this.FindControl<Button>("ActionButton")!;
        var panel = this.FindControl<WorkloadPanel>("Panel")!;
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        for (var i = 0; i < 24; i++)
        {
            _model.Title = $"Iteration {i}";
            _model.Items.Add($"Row {i}");
            if (_model.Items.Count > 4) _model.Items.RemoveAt(0);
            panel.DirectValue = i;
            panel.SetValue(WorkloadPanel.MarkerProperty, i);
            panel.Width = 260 + i;
            button.Classes.Set("accent", i % 2 == 0);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.InvalidateMeasure();
            panel.Measure(new Size(600, 600));
            panel.Arrange(new Rect(0, 0, 600, 600));
        }
        Resources["SurfaceBrush"] = Avalonia.Media.Brushes.Teal;
        Dispatcher.UIThread.Post(() => _model.Title = "Dispatcher completed", DispatcherPriority.Background);
        if (clicks != 24 || panel.DirectValue != 23 || _model.Items.Count != 4)
            throw new InvalidOperationException("Instrumentation changed application behavior.");
        Console.WriteLine("WORKLOAD_OK clicks=24 direct=23 items=4");
    }
}

public sealed class WorkloadPanel : StackPanel
{
    public static readonly DirectProperty<WorkloadPanel, int> DirectValueProperty =
        AvaloniaProperty.RegisterDirect<WorkloadPanel, int>(nameof(DirectValue), p => p.DirectValue, (p, v) => p.DirectValue = v);
    public static readonly AttachedProperty<int> MarkerProperty =
        AvaloniaProperty.RegisterAttached<WorkloadPanel, Control, int>("Marker");
    private int _directValue;
    public int DirectValue { get => _directValue; set => SetAndRaise(DirectValueProperty, ref _directValue, value); }
    protected override Size MeasureOverride(Size availableSize) => base.MeasureOverride(availableSize);
    protected override Size ArrangeOverride(Size finalSize) => base.ArrangeOverride(finalSize);
}

public sealed class WorkloadModel : INotifyPropertyChanged
{
    private string _title = "Capture integration";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Items { get; } = ["First", "Second"];
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }
}
