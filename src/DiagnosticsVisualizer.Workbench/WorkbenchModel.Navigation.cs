namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchModel
{
    private readonly record struct NavigationPoint(long EventId, long ObjectId);
    private readonly List<NavigationPoint> _navigation = [new(0, 0)];
    private int _navigationPosition;
    public int NavigationCount => _navigation.Count;
    public bool CanNavigateBack => NavigationIndex(-1) >= 0;
    public bool CanNavigateForward => NavigationIndex(1) >= 0;
    public bool NavigateBack() => Navigate(-1);
    public bool NavigateForward() => Navigate(1);
    private void RecordNavigation()
    {
        var point = new NavigationPoint(_selectedEvent, _selectedObject);
        if (_navigation[_navigationPosition] == point) return;
        if (_navigationPosition + 1 < _navigation.Count) _navigation.RemoveRange(_navigationPosition + 1, _navigation.Count - _navigationPosition - 1);
        _navigation.Add(point);
        if (_navigation.Count > 128) _navigation.RemoveAt(0);
        _navigationPosition = _navigation.Count - 1; NavigationChanged();
    }
    private void ResetNavigation()
    {
        _navigation.Clear(); _navigation.Add(new(0, 0)); _navigationPosition = 0; NavigationChanged();
    }
    private int NavigationIndex(int direction)
    {
        for (var i = _navigationPosition + direction; i >= 0 && i < _navigation.Count; i += direction)
        {
            var point = _navigation[i];
            if (point.EventId != 0 ? _events.ContainsKey(point.EventId) : point.ObjectId == 0 || Trace?.Objects.ContainsKey(point.ObjectId) == true) return i;
        }
        return -1;
    }
    private bool Navigate(int direction)
    {
        var index = NavigationIndex(direction); if (index < 0) return false;
        _navigationPosition = index; var point = _navigation[index];
        _selectedEvent = point.EventId; _selectedObject = point.ObjectId;
        SelectionChanged?.Invoke(_events.TryGetValue(point.EventId, out var e) ? e : null, point.ObjectId, false);
        NavigationChanged(); return true;
    }
    private void NavigationChanged() { Raise(nameof(CanNavigateBack)); Raise(nameof(CanNavigateForward)); Raise(nameof(NavigationCount)); }
}
