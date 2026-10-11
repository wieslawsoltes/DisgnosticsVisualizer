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
    private async Task OpenAsync(bool baseline)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File storage is unavailable in this host.");
        var files = await storage.OpenFilePickerAsync(new()
        {
            Title = baseline ? "Open baseline trace" : "Open Avalonia trace", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Avalonia binary trace") { Patterns = ["*.avtrace"], MimeTypes = ["application/octet-stream"] }]
        });
        if (files.Count == 0) return;
        var properties = await files[0].GetBasicPropertiesAsync();
        if (OperatingSystem.IsBrowser() && properties.Size > 512UL * 1024 * 1024)
            throw new InvalidOperationException("Browser imports are limited to 512 MiB. Use the desktop workbench for larger files.");
        await using var stream = await files[0].OpenReadAsync();
        if (baseline) await _model.CompareAsync(stream); else await _model.ImportAsync(stream, files[0].Name);
    }
    private async Task SaveAsync(bool chrome)
    {
        if (_model.Trace is null) throw new InvalidOperationException("Open or record a trace before exporting.");
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("File storage is unavailable in this host.");
        var file = await storage.SaveFilePickerAsync(new()
        {
            Title = chrome ? "Export Chrome Trace JSON" : "Save retained trace", SuggestedFileName = $"capture-{_model.Trace.SessionId:N}.{(chrome ? "json" : "avtrace")}",
            DefaultExtension = chrome ? "json" : "avtrace", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(chrome ? "Chrome Trace JSON" : "Avalonia binary trace") { Patterns = [chrome ? "*.json" : "*.avtrace"] }]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync(); await _model.ExportAsync(stream, chrome);
    }
    private void ShowConnection()
    {
        var stack = new StackPanel { Spacing = 12, Margin = new(26) };
        stack.Children.Add(new TextBlock { Text = "Connect to an Avalonia application", FontSize = 19, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = "The target sends capture batches to the collector. Credentials stay in memory and are never added to the URL.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ChartPalette.Muted });
        var endpoint = new TextBox { Text = "ws://127.0.0.1:4317/trace", Watermark = "wss://collector.example/trace", FontSize = 12 };
        var token = new TextBox { Watermark = "Capture token (32+ characters)", PasswordChar = '●', FontSize = 12 };
        AutomationProperties.SetName(endpoint, "Collector endpoint"); AutomationProperties.SetName(token, "Capture authentication token");
        stack.Children.Add(new TextBlock { Text = "COLLECTOR ENDPOINT", FontSize = 10, Foreground = ChartPalette.Accent }); stack.Children.Add(endpoint);
        stack.Children.Add(new TextBlock { Text = "CAPTURE TOKEN", FontSize = 10, Foreground = ChartPalette.Accent }); stack.Children.Add(token);
        stack.Children.Add(new TextBlock { Text = "Remote connections require WSS/TLS with a trusted certificate. HTTPS-hosted browser viewers may also require WSS for a local collector.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = ChartPalette.Muted });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#FFC5C5")) }; stack.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => { token.Text = ""; CloseOverlay(); };
        var connect = new Button { Content = "Connect securely" }; connect.Classes.Add("dv-primary");
        connect.Click += async (_, _) =>
        {
            connect.IsEnabled = false;
            try { await _model.ConnectAsync(new Uri(endpoint.Text ?? ""), token.Text ?? ""); token.Text = ""; CloseOverlay(); }
            catch (Exception failure) { error.Text = $"{failure.GetType().Name}: {failure.Message}"; }
            finally { connect.IsEnabled = true; }
        };
        actions.Children.Add(cancel); actions.Children.Add(connect); stack.Children.Add(actions);
        ShowCard(stack, 560); Dispatcher.UIThread.Post(() => endpoint.Focus());
    }
    private void RegisterCommands()
    {
        _commands.Add(("Open trace", "Ctrl+O", () => OpenAsync(false)));
        _commands.Add(("Save retained trace", "Ctrl+S", () => SaveAsync(false)));
        _commands.Add(("Export Chrome Trace JSON", "", () => SaveAsync(true)));
        _commands.Add(("Compare with baseline trace", "", () => OpenAsync(true)));
        _commands.Add(("Connect to collector", "Ctrl+L", () => { ShowConnection(); return Task.CompletedTask; }));
        _commands.Add(("Disconnect collector", "", _model.DisconnectAsync));
        _commands.Add(("Pause / resume target recording", "F8", _model.ToggleRecordingAsync));
        _commands.Add(("Request fresh tree checkpoint", "", _model.RequestSnapshotAsync));
        _commands.Add(("Fit timeline", "F", () => { _timeline.Fit(); _flame.Fit(); return Task.CompletedTask; }));
        _commands.Add(("Open synthetic example", "", () => { _model.LoadDemo(); return Task.CompletedTask; }));
        foreach (var panel in new[] { ("timeline", "Timeline"), ("flame", "Flame chart"), ("causality", "Causal chain"), ("systems", "Subsystem map"),
            ("visual", "Visual tree"), ("logical", "Logical tree"), ("properties", "Property inspector"), ("details", "Event details"), ("events", "Event log"), ("findings", "Findings"), ("costs", "Hot paths") })
        {
            var id = panel.Item1; _commands.Add(($"Show {panel.Item2}", "", () => { _factory.Activate(id); return Task.CompletedTask; }));
        }
    }
    private void ShowCommands()
    {
        var search = new TextBox { Watermark = "Type a command…", Margin = new(18, 16), FontSize = 14 };
        var list = new ListBox { MaxHeight = 390, Margin = new(8, 0, 8, 12) };
        var filtered = _commands.ToArray();
        void Filter() { filtered = _commands.Where(command => command.Name.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(); list.ItemsSource = filtered.Select(command => command.Name + (command.Shortcut.Length == 0 ? "" : $"    {command.Shortcut}")).ToArray(); list.SelectedIndex = filtered.Length > 0 ? 0 : -1; }
        async Task Execute() { var index = list.SelectedIndex; if (index < 0 || index >= filtered.Length) return; var command = filtered[index]; CloseOverlay(); await RunAsync(command.Run); }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { await Execute(); e.Handled = true; }
            else if (e.Key == Key.Down) { list.SelectedIndex = Math.Min(filtered.Length - 1, list.SelectedIndex + 1); e.Handled = true; }
            else if (e.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); e.Handled = true; }
        };
        list.DoubleTapped += async (_, _) => await Execute();
        var panel = new Grid { RowDefinitions = new("Auto,*") }; panel.Children.Add(search); Grid.SetRow(list, 1); panel.Children.Add(list); Filter(); ShowCard(panel, 560);
        Dispatcher.UIThread.Post(() => search.Focus());
    }
    private void ShowCard(Control content, double width)
    {
        _overlay.Child = new Border { Width = width, MaxWidth = Math.Max(300, Bounds.Width - 48), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#2B2D30")), BorderBrush = new SolidColorBrush(Color.Parse("#4A4D50")), BorderThickness = new(1), CornerRadius = new(12), Child = content };
        _overlay.IsVisible = true;
    }
    private void CloseOverlay() { _overlay.IsVisible = false; _overlay.Child = null; Focus(); }
    private async void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _overlay.IsVisible) { CloseOverlay(); e.Handled = true; return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.P)
        { ShowCommands(); e.Handled = true; return; }
        if (_overlay.IsVisible) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.B) { e.Handled = true; _model.AddBookmark(); }
            else if (e.Key == Key.O) { e.Handled = true; await RunAsync(() => OpenAsync(false)); }
            else if (e.Key == Key.S) { e.Handled = true; await RunAsync(() => SaveAsync(false)); }
            else if (e.Key == Key.L) { e.Handled = true; ShowConnection(); }
        }
        else if (e.Key == Key.F12) { e.Handled = true; _factory.Activate("sources"); }
        else if (e.Key == Key.F8) { e.Handled = true; await RunAsync(_model.ToggleRecordingAsync); }
        else if (e.Key == Key.F && e.Source is not TextBox) { _timeline.Fit(); _flame.Fit(); e.Handled = true; }
    }
    private async Task RunAsync(Func<Task> action)
    { try { await action(); } catch (OperationCanceledException) { } catch (Exception error) { _model.ReportError($"{error.GetType().Name}: {error.Message}"); } }
}
