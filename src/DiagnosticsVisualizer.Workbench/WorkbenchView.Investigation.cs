using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private readonly DispatcherOperationsControl _dispatcher = new();
    private readonly XamlOriginsControl _xaml = new();
    private XamlSourceIndex? _xamlIndex;
    public void ShowDispatcher() => _factory.Activate("dispatcher");
    public void ShowXamlOrigins() => _factory.Activate("xaml");
    private void InitializeInvestigation()
    {
        _commands.Add(("Show Dispatcher queue", "", () => { ShowDispatcher(); return Task.CompletedTask; }));
        _commands.Add(("Show XAML origins", "", () => { ShowXamlOrigins(); return Task.CompletedTask; }));
        _dispatcher.EventSelected += id => _model.SelectEvent(id, false);
        _dispatcher.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _xaml.ObjectSelected += id => _model.SelectObject(id, false);
        _xaml.SourceRequested += id => { _model.SelectEvent(id, false); _factory.Activate("sources"); };
    }
    private void UpdateInvestigation(TraceView trace, AnalysisResult analysis)
    {
        _xamlIndex = analysis.XamlSources ?? new(trace); _dispatcher.SetTrace(trace, analysis.DispatcherOperations); _xaml.Inspect(_xamlIndex, _model.SelectedObjectId);
    }
    private void SelectInvestigation(TraceView trace, TraceEvent? selected, long objectId)
    {
        if (_xamlIndex is null) _xamlIndex = new(trace);
        _xaml.Inspect(_xamlIndex, objectId);
        // An explicit event selection wins; tree selection with no event resolves a recorded object origin.
        _sources.Inspect(trace, selected ?? _xamlIndex.Find(objectId)?.Event);
    }
}
