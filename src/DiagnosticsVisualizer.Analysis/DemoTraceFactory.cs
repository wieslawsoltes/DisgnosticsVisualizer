using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

/// <summary>Deterministic, explicitly synthetic example. Never presented as a recording of the viewer itself.</summary>
public static class DemoTraceFactory
{
    public static IReadOnlyList<TraceBlock> Create(int frames = 180)
    {
        if (frames is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(frames));
        var session = Guid.Parse("75d8a72c-581b-4b6a-a9ad-98ead2cefc10");
        var symbols = new[]
        {
            new Symbol(33, "ResourceNodeExtensions.TryFindResource"), new(34, "ResourceDictionary.TryGetResource"),
            new(35, "key: SystemAccentColor"), new(36, "key: Dark"), new(37, "key: OptionalAccent"),
            new Symbol(1, "DispatcherOperation.InvokeCore"), new(2, "AnimationInstance.Step"),
            new(3, "DashboardViewModel.PropertyChanged: SelectedItem"), new(4, "BindingExpression.PublishValue"),
            new(5, "AvaloniaObject.RaisePropertyChanged: Content"), new(6, "Layoutable.InvalidateMeasure"),
            new(7, "StyledElement.ApplyStyling"), new(8, "Selector.Match: Button.primary:pointerover"),
            new(9, "DynamicResource.TryGetResource: SystemAccentColor"), new(10, "ControlTheme.TryAttach"),
            new(11, "TemplatedControl.ApplyTemplate"), new(12, "DataTemplate.Build"),
            new(13, "StyledElement.OnAttachedToLogicalTree"), new(14, "Visual.OnAttachedToVisualTree"),
            new(15, "ObservableCollection.CollectionChanged: Add"), new(16, "Layoutable.Measure"),
            new(17, "Layoutable.Arrange"), new(18, "CompositionTarget.Render"),
            new(19, "Compositor.Commit"), new(20, "ServerCompositor.Render"),
            new(21, "Interactive.RaiseEvent: PointerPressed"), new(22, "!XamlIlPopulate: DashboardView"),
            new(23, "StaticResource.ProvideValue: CardPadding"), new(24, "Application.Frame"),
            new(25, "Dispatcher.Enqueue"), new(26, "Trace checkpoint"), new(27, "DashboardViewModel.RefreshAsync"),
            new(29, "PointerPressed"), new(30, "Dashboard.OnPreviewPointer"), new(31, "Button.OnPointerPressed"), new(32, "Dashboard.OnBubblePointer"),
            new(28, SourceSiteSymbol.Encode(new("SyntheticDemo", Guid.Parse("a0a5cc80-3130-484c-ac81-dc83cd4d08ea"), 0x06000001, "Dashboard.XamlIlPopulate", "SyntheticDashboard.axaml", 12, 1, 12, 20, "", "", "", false) { IlOffset = 16, Mapping = SourceMappingKind.XamlConstruction }))
        };
        var nodes = new List<NodeSnapshot>
        {
            new(1, 0, 0, 0, "Avalonia.Controls.Window", "Dashboard", 0, 0, 1280, 800, true, ""),
            new(2, 1, 1, 0, "Avalonia.Controls.Grid", "Shell", 0, 0, 1280, 800, true, "shell"),
            new(3, 2, 2, 0, "Avalonia.Controls.StackPanel", "Navigation", 0, 0, 220, 800, true, "navigation"),
            new(4, 2, 2, 0, "Avalonia.Controls.ContentControl", "Workspace", 220, 0, 1060, 800, true, "workspace"),
            new(5, 3, 3, 0, "Avalonia.Controls.Button", "RefreshButton", 16, 88, 188, 36, true, "primary"),
            new(6, 5, 0, 5, "Avalonia.Controls.Border", "PART_Border", 0, 0, 188, 36, true, ""),
            new(7, 6, 0, 5, "Avalonia.Controls.Presenters.ContentPresenter", "PART_ContentPresenter", 12, 6, 164, 24, true, ""),
            new(8, 7, 5, 5, "Avalonia.Controls.TextBlock", "RefreshLabel", 0, 0, 90, 24, true, ""),
            new(9, 4, 4, 0, "Avalonia.Controls.ItemsControl", "Results", 24, 96, 1012, 600, true, "results"),
            new(10, 4, 4, 0, "Avalonia.Controls.TextBlock", "Title", 24, 24, 460, 48, true, "h1")
        };
        for (var i = 0; i < 28; i++) nodes.Add(new(20 + i, 9, 9, 9, "Avalonia.Controls.Border", $"ResultCard{i + 1}",
            i % 4 * 250, i / 4 * 80, 238, 68, true, "result-card"));
        var properties = nodes.SelectMany(n => new[]
        {
            new PropertySnapshot(n.Id, "Bounds", "Direct", $"{n.X}, {n.Y}, {n.Width}, {n.Height}", "LocalValue", "Layoutable.Arrange"),
            new(n.Id, "IsVisible", "Styled", "true", "Inherited", "Visual"),
            new(n.Id, "Margin", "Styled", "0, 0, 0, 0", "Style", ".result-card"),
            new(n.Id, "DataContext", "Styled", "<DashboardViewModel>", "Inherited", "Window.DataContext")
        }).Concat(new[]
        {
            new PropertySnapshot(5, "Background", "Styled", "<ImmutableSolidColorBrush>", "StyleTrigger", "Button.primary:pointerover"),
            new(5, "Background", "Style frame · active", "<ImmutableSolidColorBrush>", "StyleTrigger", "Button.primary:pointerover"),
            new(5, "Background", "Theme frame · active", "<ImmutableSolidColorBrush>", "Template", "Button ControlTheme"),
            new(5, "Text", "Styled", "<text redacted>", "LocalValue", "Binding: RefreshLabel"),
            new(5, "SystemAccentColor", "Local resource", "<Color>", "Resource dictionary", "Application.Resources")
        }).ToArray();
        var snapshot = new SnapshotData(nodes.ToArray(), properties,
            [new(5, 100, "ControlTheme", "Fluent Button"), new(5, 101, "Style", "Button.primary:pointerover"),
             new(5, 102, "DynamicResource", "SystemAccentColor"), new(4, 103, "DataContext", "DashboardViewModel"),
             new(9, 104, "ItemsSource", "ObservableCollection<Result>"), new(9, 105, "DataTemplate", "ResultCardTemplate")], 5);
        var objects = nodes.Select(n => new ObjectInfo(n.Id, n.Type, string.IsNullOrEmpty(n.Name) ? n.Type : n.Name))
            .Concat([new ObjectInfo(100, "ControlTheme", "Fluent Button"), new(101, "Style", "Button.primary:pointerover"),
                new(102, "Resource", "SystemAccentColor"), new(103, "DashboardViewModel", "DashboardViewModel"),
                new(104, "ObservableCollection<Result>", "Results"), new(105, "DataTemplate", "ResultCardTemplate"), new(106, "DispatcherOperation", "Synthetic dispatcher admission"), new(107, "RoutedEvent", "Synthetic PointerPressed"), new(108, "ResourceDictionary", "Window resources"), new(109, "ResourceDictionary", "Theme resources"), new(110, "SolidColorBrush", "Synthetic resource value"), new(111, "System.String", "Resource key"), new(112, "System.String", "Dark")]).ToArray();
        var events = new List<TraceEvent>(frames * 36); long id = 0;
        events.Add(new(++id, 0, 0, 0, 5, 0, 1, 28, Subsystem.Xaml, TraceEventKind.Link, XamlObjectEvent.ConstructionCode));
        for (var f = 0; f < frames; f++)
        {
            var start = f * 16667L; var heavy = f % 43 == 12; var measure = heavy ? 14200L : 2400L + f % 7 * 140;
            var queue = ++id;
            events.Add(new(queue, 0, start, 0, 106, 0, 8, 25, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.QueuedCode, DispatcherEvent.PackPriority(-6, false)));
            events.Add(new(++id, queue, start + 30, 0, 106, 0, 1, 25, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.PriorityCode, DispatcherEvent.PackPriority(1, true)));
            events.Add(new(++id, queue, start + 50, 0, 106, 0, 1, 1, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.StartedCode, DispatcherEvent.PackPriority(1, true)));
            events.Add(new(++id, queue, start + 50 + measure, 0, 106, 0, 1, 1, Subsystem.Dispatcher, TraceEventKind.Link, DispatcherEvent.FinishedCode, (long)DispatcherOutcome.Returned));
            var root = ++id; var dispatcher = ++id;
            events.Add(new(root, 0, start, measure + 11200, 1, 0, 1, 24, Subsystem.Application));
            events.Add(new(dispatcher, root, start + 50, measure + 10700, 1, 0, 1, 1, Subsystem.Dispatcher));
            void Span(int symbol, Subsystem category, long offset, long duration, long obj, long parent = 0, int thread = 1)
                => events.Add(new(++id, parent == 0 ? dispatcher : parent, start + offset, duration, obj, 0, thread, symbol, category));
            Span(2, Subsystem.Animation, 100, 230, 5);
            Span(21, Subsystem.RoutedEvents, 340, 120, 5);
            if (f % 15 == 0)
            {
                // Explicitly synthetic route observations, with exact per-dispatch grouping and nesting.
                var routeId = ++id;
                events.Add(new(routeId, dispatcher, start + 340, 120, 5, 107, 1, 29, Subsystem.RoutedEvents, TraceEventKind.Span,
                    RoutedDispatchEvent.Pack(RoutedStepKind.Dispatch, 6, false, false, false, true), routeId));
                events.Add(new(++id, routeId, start + 340, 0, 5, 107, 1, 29, Subsystem.RoutedEvents, TraceEventKind.Link,
                    RoutedDispatchEvent.Pack(RoutedStepKind.Begin, 6, false, false, false, false), routeId));
                void Step(int symbol, long target, int phase, long offset, long duration, RoutedStepKind kind, bool before, bool after, bool too = false)
                    => events.Add(new(++id, routeId, start + offset, duration, target, 5, 1, symbol, Subsystem.RoutedEvents,
                        kind is RoutedStepKind.SkippedHandled or RoutedStepKind.SkippedRoute ? TraceEventKind.Instant : TraceEventKind.Span,
                        RoutedDispatchEvent.Pack(kind, phase, before, after, too, kind is not (RoutedStepKind.SkippedHandled or RoutedStepKind.SkippedRoute)), routeId));
                Step(29, 1, 2, 341, 4, RoutedStepKind.ClassListeners, false, false);
                Step(30, 1, 2, 347, 20, RoutedStepKind.Handler, false, true);
                Step(31, 5, 2, 370, 0, RoutedStepKind.SkippedHandled, true, true);
                Step(31, 5, 2, 373, 18, RoutedStepKind.Handler, true, false, true);
                Step(31, 5, 4, 395, 23, RoutedStepKind.Handler, false, false);
                Step(32, 1, 4, 425, 22, RoutedStepKind.Handler, false, false);
            }

            if (f % 30 == 0)
            {
                var operation = ++id;
                events.Add(new(operation, dispatcher, start + 480, 0, 103, 0, 1, 27, Subsystem.Application, TraceEventKind.Link, AsyncOperationEvent.BeginCode));
                events.Add(new(++id, operation, start + 40000 + f * 180, 0, 103, 0, 8, 27, Subsystem.Application,
                    TraceEventKind.Link, AsyncOperationEvent.EndCode, (long)(f % 90 == 60 ? AsyncOperationOutcome.Canceled : AsyncOperationOutcome.Succeeded)));
            }
            var notification = ++id; events.Add(new(notification, dispatcher, start + 480, 850, 103, 0, 1, 3, Subsystem.PropertyNotifications));
            var binding = ++id; events.Add(new(binding, notification, start + 520, 660, 5, 103, 1, 4, Subsystem.Bindings));
            Span(5, Subsystem.Properties, 600, 460, 5, binding);
            Span(6, Subsystem.Layout, 720, 110, 5, id);
            Span(7, Subsystem.Styles, 1400, 1200, 5);
            var styling = id; Span(8, Subsystem.Selectors, 1440, 280, 5, styling);
            Span(9, Subsystem.Resources, 1820, 190, 5, styling);
            if (f % 12 == 0)
            {
                long Lookup(long parentLookup, long offset, long duration, long provider, ResourceLookupOutcome outcome, int symbol, int keyName)
                {
                    var lookup = ++id; var at = start + offset;
                    events.Add(new(++id, lookup, at, 0, 111, provider, 1, keyName, Subsystem.Resources, TraceEventKind.Link, ResourceLookupEvent.RequestCode, parentLookup));
                    events.Add(new(++id, lookup, at, 0, 112, 0, 1, 36, Subsystem.Resources, TraceEventKind.Link, ResourceLookupEvent.ThemeCode, parentLookup));
                    events.Add(new(lookup, parentLookup == 0 ? styling : parentLookup, at, duration, provider, outcome == ResourceLookupOutcome.Found ? 110 : 0, 1, symbol,
                        Subsystem.Resources, TraceEventKind.Span, ResourceLookupEvent.CompleteCode(outcome), parentLookup));
                    return lookup;
                }
                var missing = f % 36 == 24; var keyName = missing ? 37 : 35;
                var lookup = Lookup(0, 1820, 180, 5, missing ? ResourceLookupOutcome.Missing : ResourceLookupOutcome.Found, 33, keyName);
                Lookup(lookup, 1830, 35, 108, ResourceLookupOutcome.Missing, 34, keyName);
                Lookup(lookup, 1875, 110, 109, missing ? ResourceLookupOutcome.Missing : ResourceLookupOutcome.Found, 34, keyName);
            }
            Span(23, Subsystem.Resources, 2050, 110, 5, styling);
            Span(10, Subsystem.ControlThemes, 2650, 190, 5);
            Span(11, Subsystem.Templates, 2900, 1000, 5);
            var template = id; Span(12, Subsystem.DataTemplates, 3000, 740, 9, template);
            Span(13, Subsystem.LogicalTree, 3130, 100, 20 + f % 28, id);
            Span(14, Subsystem.VisualTree, 3290, 120, 20 + f % 28, template);
            Span(15, Subsystem.CollectionNotifications, 4000, 140, 104);
            if (f % 30 == 0) Span(22, Subsystem.Xaml, 4240, 280, 4);
            Span(16, Subsystem.Layout, 4600, measure, 4);
            var measureId = id;
            for (var child = 0; child < 8; child++) Span(16, Subsystem.Layout, 4630 + child * measure / 9,
                measure / 12, 20 + (child + f) % 28, measureId);
            Span(17, Subsystem.Layout, 4800 + measure, 1100, 4);
            Span(18, Subsystem.Rendering, 6100 + measure, 2700, 4);
            Span(19, Subsystem.Compositor, 8900 + measure, 400, 1);
            events.Add(new(++id, 0, start + 9400 + measure, 1600, 1, 0, 8, 20, Subsystem.Compositor));
        }
        var blocks = new List<TraceBlock>(); var chunks = events.Chunk(2048).ToArray();
        for (var i = 0; i < chunks.Length; i++) blocks.Add(new()
        {
            SessionId = session, Sequence = i + 1, Frequency = 1_000_000, OriginTimestamp = 0,
            UtcStartTicks = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc).Ticks,
            Recording = false, Symbols = symbols, Objects = objects, Events = chunks[i], Snapshot = snapshot with
            {
                Nodes = snapshot.Nodes.Select(node => node.Id == 5 ? node with { Width = 188 + i * 8, Classes = i == 0 ? "primary" : "primary:pointerover" } : node).ToArray(),
                Properties = snapshot.Properties.Select(property => property.ObjectId == 5 && property.Name == "Bounds"
                    ? property with { Value = $"16, 88, {188 + i * 8}, 36" } : property).ToArray()
            }
        });
        return blocks;
    }
}
