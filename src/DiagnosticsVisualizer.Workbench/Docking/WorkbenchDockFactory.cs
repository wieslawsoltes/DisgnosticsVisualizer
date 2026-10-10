using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;

namespace DiagnosticsVisualizer.Workbench.Docking;

public sealed class PanelDocument : Document
{
    public required Control View { get; init; }
}
public sealed class PanelTool : Tool
{
    public required Control View { get; init; }
}

public sealed class WorkbenchDockFactory(IReadOnlyDictionary<string, Control> views) : Factory
{
    private readonly Dictionary<string, IDockable> _panels = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IDockable> Panels => _panels;
    public override IRootDock CreateLayout()
    {
        _panels.Clear();
        PanelDocument Document(string id, string title)
        {
            var panel = new PanelDocument { Id = id, Title = title, View = views[id], CanClose = false, CanFloat = true };
            _panels[id] = panel; return panel;
        }
        PanelTool Tool(string id, string title)
        {
            var panel = new PanelTool { Id = id, Title = title, View = views[id], CanClose = false, CanFloat = true, CanPin = true };
            _panels[id] = panel; return panel;
        }
        var timeline = Document("timeline", "Timeline"); var flame = Document("flame", "Flame chart");
        var causal = Document("causality", "Causal chain"); var systems = Document("systems", "Subsystems");
        var comparison = Document("comparison", "Compare");
        var visual = Tool("visual", "Visual tree"); var logical = Tool("logical", "Logical tree");
        var properties = Tool("properties", "Properties & resources"); var details = Tool("details", "Event details");
        var events = Tool("events", "Event log"); var findings = Tool("findings", "Findings"); var costs = Tool("costs", "Hot paths");
        var left = new ToolDock { Id = "left", Alignment = Alignment.Left, Proportion = .19, ActiveDockable = visual,
            VisibleDockables = CreateList<IDockable>(visual, logical) };
        var right = new ToolDock { Id = "right", Alignment = Alignment.Right, Proportion = .26, ActiveDockable = properties,
            VisibleDockables = CreateList<IDockable>(properties, details) };
        var documents = new DocumentDock { Id = "documents", IsCollapsable = false, Proportion = .68, ActiveDockable = timeline,
            VisibleDockables = CreateList<IDockable>(timeline, flame, causal, systems, comparison), CanCreateDocument = false };
        var bottom = new ToolDock { Id = "bottom", Alignment = Alignment.Bottom, Proportion = .32, ActiveDockable = findings,
            VisibleDockables = CreateList<IDockable>(events, findings, costs) };
        var center = new ProportionalDock { Id = "center", Orientation = Dock.Model.Core.Orientation.Vertical, Proportion = .55,
            VisibleDockables = CreateList<IDockable>(documents, new ProportionalDockSplitter(), bottom) };
        var main = new ProportionalDock { Id = "main", Orientation = Dock.Model.Core.Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), center, new ProportionalDockSplitter(), right) };
        var root = CreateRootDock(); root.Id = "Root"; root.IsCollapsable = false; root.ActiveDockable = main; root.DefaultDockable = main;
        root.VisibleDockables = CreateList<IDockable>(main);
        root.LeftPinnedDockables = CreateList<IDockable>(); root.RightPinnedDockables = CreateList<IDockable>();
        root.TopPinnedDockables = CreateList<IDockable>(); root.BottomPinnedDockables = CreateList<IDockable>();
        return root;
    }
    public override void InitLayout(IDockable layout)
    {
        ContextLocator = views.ToDictionary(pair => pair.Key, pair => (Func<object?>)(() => pair.Value));
        DockableLocator = _panels.ToDictionary(pair => pair.Key, pair => (Func<IDockable?>)(() => pair.Value));
        DockableLocator["Root"] = () => layout;
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        { [nameof(IDockWindow)] = () => DockSettings.UseManagedWindows ? new ManagedHostWindow() : new HostWindow { Title = "Diagnostics Visualizer" } };
        base.InitLayout(layout);
    }
    public void Activate(string id)
    {
        if (!_panels.TryGetValue(id, out var panel) || panel.Owner is not IDock dock) return;
        dock.ActiveDockable = panel;
        SetFocusedDockable(dock, panel);
    }
}
