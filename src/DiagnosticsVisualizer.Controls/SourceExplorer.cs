using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Controls;

public sealed record SourceRow(int SiteId, SourceSite Site)
{
    public string Assembly => Site.Assembly;
    public string Method => Site.Signature;
    public string Location => Site.Location;
}

/// <summary>Shared desktop/WASM source navigation. Source content stays local and is never executed.</summary>
public sealed class SourceExplorer : UserControl
{
    private readonly DataGrid _sites = DiagnosticGrid.Create(("Assembly", "Assembly", 160), ("Source location", "Location", 200), ("Method", "Method", -1));
    private readonly TextBlock _status = new() { Text = "Select an event to inspect its original source identity.", TextWrapping = TextWrapping.Wrap, Margin = new(12) };
    private readonly TextBlock _identity = new() { TextWrapping = TextWrapping.Wrap, Margin = new(12, 4), FontSize = 11 };
    private readonly TextBox _code = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = FontFamily.Parse("monospace"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    private readonly Button _open = new() { Content = "Open matching source file", IsEnabled = false };
    private readonly Button _copy = new() { Content = "Copy SourceLink URL", IsEnabled = false };
    private SourceSite? _selected;
    private TraceView? _trace;
    private SourceRow[] _rows = [];
    private long _revision;
    public SourceSite? SelectedSite => _selected;
    public string StatusText => _status.Text ?? "";
    public string SourceText => _code.Text ?? "";
    public SourceExplorer()
    {
        AutomationProperties.SetName(this, "Sources explorer");
        AutomationProperties.SetName(_code, "Verified source excerpt");
        var root = new Grid { RowDefinitions = new("Auto,150,Auto,Auto,*") };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(12, 6) };
        toolbar.Children.Add(_open); toolbar.Children.Add(_copy);
        root.Children.Add(_status); Grid.SetRow(_sites, 1); root.Children.Add(_sites);
        Grid.SetRow(toolbar, 2); root.Children.Add(toolbar); Grid.SetRow(_identity, 3); root.Children.Add(_identity);
        Grid.SetRow(_code, 4); root.Children.Add(_code); Content = root;
        _sites.AttachedToVisualTree += (_, _) => RevealSite();
        _sites.SelectionChanged += (_, _) => { if (_sites.SelectedItem is SourceRow row) Select(row.Site); };
        _open.Click += async (_, _) =>
        {
            try
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage is null) return;
                var revision = _revision;
                var files = await storage.OpenFilePickerAsync(new() { Title = "Choose the source file from the captured build", AllowMultiple = false });
                if (files.Count == 0 || revision != _revision) return;
                using var file = files[0]; await using var stream = await file.OpenReadAsync();
                await ShowSourceAsync(stream);
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        _copy.Click += async (_, _) =>
        {
            try
            {
                if (_selected is { SourceUrl.Length: > 0 } site && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(site.SourceUrl);
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
    }

    public void Inspect(TraceView trace, TraceEvent? selected)
    {
        if (!ReferenceEquals(_trace, trace))
        {
            _trace = trace;
            _rows = trace.SourceSites.OrderByDescending(p => p.Value.Line > 0).ThenBy(p => p.Value.Assembly, StringComparer.Ordinal)
                .ThenBy(p => p.Value.Signature, StringComparer.Ordinal).Select(p => new SourceRow(p.Key, p.Value)).ToArray();
            _sites.ItemsSource = _rows;
        }
        _sites.SelectedItem = selected is { } e ? _rows.FirstOrDefault(r => r.SiteId == e.NameId) : null;
        Select(selected is { } ev ? trace.SourceSites.GetValueOrDefault(ev.NameId) : null);
        RevealSite();
    }

    private void RevealSite()
    {
        if (_sites.SelectedItem is { } item)
            Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_sites.SelectedItem, item)) _sites.ScrollIntoView(item, null); }, DispatcherPriority.Loaded);
    }

    private void Select(SourceSite? site)
    {
        if (_selected == site) return;
        _revision++; _selected = site; _code.Text = "";
        _open.IsEnabled = site is { Line: > 0 }; _copy.IsEnabled = site is { SourceUrl.Length: > 0 };
        _identity.Text = site is null ? "" : $"{site.Assembly} • original MVID/token {site.Identity}\n{site.HashAlgorithm} {site.Checksum}\n{site.SourceUrl}";
        _status.Text = site is null ? "This event has no source metadata. Legacy captures and notification-only sites remain supported."
            : $"{site.Location} • {(site.AsyncKickoff ? "Async kickoff: this span ends at synchronous return, not asynchronous completion." : "First visible sequence point, not an exact instruction-level sample.")}";
    }

    public async Task ShowSourceAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var site = _selected ?? throw new InvalidOperationException("Select a source site first.");
        var revision = _revision;
        var excerpt = await SourceExcerpt.ReadAsync(stream, site, cancellationToken);
        if (_revision != revision) return;
        _code.Text = excerpt.Text;
        _code.Focus(); _code.CaretIndex = excerpt.SelectionEnd;
        _code.SelectionStart = excerpt.SelectionStart; _code.SelectionEnd = excerpt.SelectionEnd;
        _status.Text = $"{site.Location} • {(excerpt.ChecksumVerified ? "PDB checksum verified" : "Unverified: the PDB supplied no checksum")} • source is local, read-only, and not included in exports.";
    }
}
