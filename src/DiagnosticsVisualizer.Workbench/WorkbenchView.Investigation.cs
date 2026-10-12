using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Workbench;

public sealed partial class WorkbenchView
{
    private readonly BindingOperationsControl _bindings = new();
    public void ShowBindings() => _factory.Activate("bindings");
    private readonly RoutedEventsControl _routed = new();
    public void ShowRoutedEvents() => _factory.Activate("routed");
    private readonly StyleEvaluationsControl _styleEvaluations = new();
    public void ShowStyleEvaluations() => _factory.Activate("styling");
    private readonly ResourceLookupsControl _resourceLookups = new();
    public void ShowResourceLookups() => _factory.Activate("resources");
    private readonly DispatcherOperationsControl _dispatcher = new();
    private readonly XamlOriginsControl _xaml = new();
    private XamlSourceIndex? _xamlIndex;
    public void ShowDispatcher() => _factory.Activate("dispatcher");
    public void ShowXamlOrigins() => _factory.Activate("xaml");
    private void InitializeInvestigation()
    {
        _factory.RegisterDocument("bindings", "Bindings", _bindings);
        _commands.Add(("Show Binding operations", "", () => { ShowBindings(); return Task.CompletedTask; }));
        _commands.Add(("Show Binding expression history", "", () => { ShowBindings(); _bindings.ShowExpressionHistory(); return Task.CompletedTask; }));
        _bindings.EventSelected += id => _model.SelectEvent(id, false);
        _bindings.ObjectSelected += id => _model.SelectObject(id, false);
        _bindings.SourceRequested += id => { _model.SelectEvent(id, false); ShowSources(); };
        _bindings.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _commands.Add(("Show Style activation history", "", () => { ShowStyleEvaluations(); _styleEvaluations.ShowActivationHistory(); return Task.CompletedTask; }));
        _commands.Add(("Show Styling evaluations", "", () => { ShowStyleEvaluations(); return Task.CompletedTask; }));
        _styleEvaluations.EventSelected += id => _model.SelectEvent(id, false);
        _styleEvaluations.ObjectSelected += id => _model.SelectObject(id, false);
        _styleEvaluations.SourceRequested += id => { _model.SelectEvent(id, false); ShowSources(); };
        _styleEvaluations.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _commands.Add(("Show Resource lookups", "", () => { ShowResourceLookups(); return Task.CompletedTask; }));
        _resourceLookups.EventSelected += id => _model.SelectEvent(id, false);
        _resourceLookups.ObjectSelected += id => _model.SelectObject(id, false);
        _resourceLookups.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _commands.Add(("Show Routed events", "", () => { ShowRoutedEvents(); return Task.CompletedTask; }));
        _routed.EventSelected += id => _model.SelectEvent(id, false);
        _routed.ObjectSelected += id => _model.SelectObject(id, false);
        _routed.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _commands.Add(("Show Dispatcher queue", "", () => { ShowDispatcher(); return Task.CompletedTask; }));
        _commands.Add(("Show XAML origins", "", () => { ShowXamlOrigins(); return Task.CompletedTask; }));
        _dispatcher.EventSelected += id => _model.SelectEvent(id, false);
        _dispatcher.RangeRequested += async range => await RunAsync(() => _model.SetRangeAsync(range));
        _xaml.ObjectSelected += id => _model.SelectObject(id, false);
        _xaml.SourceRequested += id => { _model.SelectEvent(id, false); _factory.Activate("sources"); };
    }
    private void UpdateInvestigation(TraceView trace, AnalysisResult analysis)
    {
        _bindings.SetTrace(trace, analysis.BindingOperations ?? new(trace));
        _routed.SetTrace(trace, analysis.RoutedDispatches);
        _styleEvaluations.SetTrace(trace, analysis.StyleEvaluations ?? new(trace));
        _resourceLookups.SetTrace(trace, analysis.ResourceLookups ?? new(trace));
        _xamlIndex = analysis.XamlSources ?? new(trace); _dispatcher.SetTrace(trace, analysis.DispatcherOperations); _xaml.Inspect(_xamlIndex, _model.SelectedObjectId);
    }
    private void SelectInvestigation(TraceView trace, TraceEvent? selected, long objectId)
    {
        _bindings.SelectEvent(selected); _routed.SelectEvent(selected); _styleEvaluations.SelectEvent(selected); _resourceLookups.SelectEvent(selected);
        if (_xamlIndex is null) _xamlIndex = new(trace);
        _xaml.Inspect(_xamlIndex, objectId);
        _sources.Inspect(trace, selected ?? _xamlIndex.Find(objectId)?.Event);
    }
}
