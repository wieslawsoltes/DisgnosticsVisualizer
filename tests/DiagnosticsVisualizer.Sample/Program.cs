using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DiagnosticsVisualizer.Sample;

// No diagnostics API calls: the package injects bootstrap and probes into output copies.
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
                DispatcherTimer.RunOnce(() => { window.VerifyResources(); desktop.Shutdown(0); }, TimeSpan.FromSeconds(2));
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed partial class WorkloadWindow : Window
{
    private readonly WorkloadModel _model = new();
    private int _deferredBuilds;
    public WorkloadWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
        var deferred = new ResourceDictionary();
        deferred.AddDeferred("DiagnosticsLazy", _ => { _deferredBuilds++; return "Deferred value"; });
        Resources.MergedDictionaries.Add(deferred);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["DiagnosticsVariant"] = 42 };

    }
    public void VerifyResources()
    {
        if (_deferredBuilds != 0) throw new InvalidOperationException("Diagnostics materialized a deferred resource.");
        Console.WriteLine("RESOURCE_WORKLOAD_OK deferred=0");
    }
    public async void Exercise()
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
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(80),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, .8d) } }
            }
        };
        await animation.RunAsync(button);
        await ExerciseAsync();
        await ExerciseDispatcherAsync();
    }
    private async Task ExerciseDispatcherAsync()
    {
        var completed = 0; var abortedInvocations = 0;
        var aborted = Dispatcher.UIThread.InvokeAsync(() => abortedInvocations++, DispatcherPriority.Background);
        if (!aborted.Abort()) throw new InvalidOperationException("Queued operation did not abort.");
        var promoted = Dispatcher.UIThread.InvokeAsync(() => completed++, DispatcherPriority.Inactive);
        promoted.Priority = DispatcherPriority.Background;
        await promoted;
        await Task.Run(async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _model.Title = "Dispatcher cross-thread callback"; completed++;
            }, DispatcherPriority.Background);
        });
        try
        {
            await Dispatcher.UIThread.InvokeAsync((Action)(() => throw new InvalidOperationException("Expected callback failure")));
            throw new InvalidOperationException("Callback failure was swallowed.");
        }
        catch (InvalidOperationException error) when (error.Message == "Expected callback failure") { }
        if (completed != 2 || abortedInvocations != 0) throw new InvalidOperationException("Dispatcher instrumentation changed callback behavior.");
        Console.WriteLine("DISPATCHER_WORKLOAD_OK completed=2 aborted=0 fault=observed");
    }
    private async Task ExerciseAsync()
    {
        // Not data-bound: exercise a real background INPC chain without violating UI affinity.
        var model = new WorkloadModel(); var notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;
        await Task.Run(async () =>
        {
            await Task.Delay(10).ConfigureAwait(false);
            model.Title = "Background continuation";
        });
        if (notifications != 1) throw new InvalidOperationException("Async instrumentation changed notification behavior.");
        _model.Title = "Async completed";
        Console.WriteLine("ASYNC_WORKLOAD_OK notifications=1");
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
