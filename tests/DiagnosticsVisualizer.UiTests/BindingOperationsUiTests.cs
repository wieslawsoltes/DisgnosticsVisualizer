using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Controls;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class BindingOperationsUiTests
{
    [AvaloniaFact] public async Task BindingDocumentNavigatesRecordedAttachmentAndSurvivesDockRestore()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage(); using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view }; window.Show();
        var events = new List<TraceEvent>(); var symbol = 0; var symbols = new List<Symbol>();
        void Call(long id, long time, long duration, BindingOperationKind kind, long target, BindingValueKind value)
        {
            var name = ++symbol; symbols.Add(new(name, "Deterministic fixture: " + kind));
            var facts = BindingOperationEvent.Facts(value, running: kind != BindingOperationKind.Attach, valueChanged: kind == BindingOperationKind.Deliver ? true : null);
            events.Add(new(id + 1, id, time, 0, 20, target, 1, name, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.BeginCode(kind, facts), 0));
            events.Add(new(id + 2, id, time, 0, 30, 40, 1, name, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.ContextCode(kind), 0));
            events.Add(new(id + 3, id, time, 0, 0, 0, 1, name, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.InputCode(kind), long.MinValue));
            events.Add(new(id, 0, time, duration, 20, 0, 1, name, Subsystem.Bindings, TraceEventKind.Span, BindingOperationEvent.EndCode(kind, BindingOperationOutcome.Returned, facts), 0));
        }
        Call(10, 0, 10, BindingOperationKind.Attach, 50, BindingValueKind.Unknown);
        Call(100, 20, 10, BindingOperationKind.Publish, 0, BindingValueKind.Null);
        Call(200, 50, 5, BindingOperationKind.Deliver, 0, BindingValueKind.Null);
        var block = new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000, Symbols = symbols.ToArray(), Events = events.ToArray(),
            Objects = [new(20, "BindingExpression", "Expression"), new(30, "AvaloniaProperty", "Tag"), new(40, "Sink", "Sink"), new(50, "Border", "Recorded target")] };
        using var stream = new MemoryStream(); await TraceFile.WriteAsync(stream, block); stream.Position = 0;
        await view.Model.ImportAsync(stream, "Deterministic binding fixture"); view.ShowBindings(); Dispatcher.UIThread.RunJobs();
        var control = view.GetVisualDescendants().OfType<BindingOperationsControl>().Single(); Assert.Equal(3, control.OperationCount);
        view.Model.SelectEvent(200, false); control.ShowExpressionHistory(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(BindingOperationKind.Deliver, control.SelectedOperation!.Kind); Assert.Contains("earlier successful attachment target #50", control.DetailsText);
        var target = control.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Recorded target");
        Assert.True(target.IsEnabled); target.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(50, view.Model.SelectedObjectId);
        var snapshot = view.ExportWorkspace(); view.ApplyWorkspacePreset(WorkspacePreset.Profile); view.ImportWorkspace(snapshot); Dispatcher.UIThread.RunJobs();
        Assert.Null(view.WorkspaceError); Assert.Single(view.GetVisualDescendants().OfType<BindingOperationsControl>());
        var directory = Path.Combine(AppContext.BaseDirectory, "TestResults"); Directory.CreateDirectory(directory);
        using var image = window.CaptureRenderedFrame(); Assert.NotNull(image); image.Save(Path.Combine(directory, "binding-operations.png")); window.Close();
    }
    [AvaloniaFact] public void IncompleteBindingCallsDoNotEnableFabricatedRangeAnalysis()
    {
        var store = new TraceStore(); store.Add(new TraceBlock { SessionId = Guid.NewGuid(), Sequence = 1, Frequency = 1000,
            Symbols = [new(1, "Incomplete")], Events = [new(2, 1, 10, 0, 3, 0, 1, 1, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.BeginCode(BindingOperationKind.Publish, 0), 0)] });
        var trace = store.GetView(); var control = new BindingOperationsControl();
        var window = new Window { Width = 1000, Height = 800, Content = WorkbenchStyles.CreateScope(control) }; window.Show();
        control.SetTrace(trace, new(trace)); Dispatcher.UIThread.RunJobs();
        Assert.Equal("Incomplete", control.SelectedOperation!.Status);
        Assert.False(control.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Analyze call").IsEnabled); window.Close();
    }
}
