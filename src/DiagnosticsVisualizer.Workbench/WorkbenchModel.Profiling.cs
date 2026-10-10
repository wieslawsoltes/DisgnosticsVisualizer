using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Workbench;

public sealed record TraceBookmark(long EventId, long ObjectId, long Timestamp, string Label);

public sealed partial class WorkbenchModel
{
    private TraceProfile? _profileIndex;
    private int _rangeGeneration;
    private string _queryError = "";
    private readonly List<TraceBookmark> _bookmarks = [];
    public ProfileResult? FullProfile { get; private set; }
    public ProfileResult? Profile { get; private set; }
    public TraceRange? Range { get; private set; }
    public string QueryError { get => _queryError; private set => Set(ref _queryError, value); }
    public IReadOnlyList<TraceBookmark> Bookmarks => _bookmarks;
    public event Action? ProfileUpdated;
    public event Action? BookmarksUpdated;
    private static (TraceView view, AnalysisResult analysis, TraceProfile index, ProfileResult profile, ProfileResult selected, TraceRange? range) Prepare(TraceView view, TraceRange? range = null)
    {
        var index = new TraceProfile(view);
        var full = index.Analyze();
        return (view, TraceAnalyzer.Analyze(view), index, full, range is null ? full : index.Analyze(range), range);
    }
    public async Task SetRangeAsync(TraceRange? range)
    {
        if (range is { } r && r.End <= r.Start) throw new ArgumentOutOfRangeException(nameof(range));
        var index = _profileIndex; if (index is null) return;
        var revision = ++_rangeGeneration; var generation = _generation;
        Range = range; Raise(nameof(Range));
        var profile = range is null ? FullProfile : await Task.Run(() => index.Analyze(range));
        if (revision != _rangeGeneration || generation != _generation || !ReferenceEquals(index, _profileIndex)) return;
        Profile = profile; ProfileUpdated?.Invoke();
    }
    private async Task RefreshRangeAsync(TraceRange range)
    {
        try { await SetRangeAsync(range); }
        catch (Exception error) { ReportError($"Range analysis failed: {error.Message}"); }
    }
    public void AddBookmark(string? label = null)
    {
        if (Trace is not { } trace || !_events.TryGetValue(_selectedEvent, out var e)) return;
        label = string.IsNullOrWhiteSpace(label) ? trace.Name(e) : label.Trim();
        if (label.Length > 256) label = label[..256];
        if (_bookmarks.Any(b => b.EventId == e.Id) || _bookmarks.Count >= 1000) return;
        _bookmarks.Add(new(e.Id, e.ObjectId, e.Timestamp, label)); BookmarksUpdated?.Invoke();
    }
    public void RemoveBookmark(long eventId)
    { _bookmarks.RemoveAll(b => b.EventId == eventId); BookmarksUpdated?.Invoke(); }
    private void ResetReview()
    {
        ++_rangeGeneration; Range = null; Profile = FullProfile = null; _profileIndex = null;
        _selectedEvent = _selectedObject = 0; Comparison = []; _bookmarks.Clear();
        BookmarksUpdated?.Invoke(); ComparisonUpdated?.Invoke();
        var empty = new TraceStore().GetView(); Apply(empty, TraceAnalyzer.Analyze(empty));
    }
}
