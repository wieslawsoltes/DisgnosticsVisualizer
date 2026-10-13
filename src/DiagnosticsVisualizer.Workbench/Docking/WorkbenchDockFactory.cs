using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;
using DiagnosticsVisualizer.Workbench.Persistence;

namespace DiagnosticsVisualizer.Workbench.Docking;

public sealed class PanelDocument : Document { public required Control View { get; init; } }
public sealed class PanelTool : Tool { public required Control View { get; init; } }

/// <summary>One reusable control per panel. Saved layouts contain identities, never view instances or credentials.</summary>
public sealed class WorkbenchDockFactory(IReadOnlyDictionary<string, Control> views) : Factory
{
    private readonly Dictionary<string, Control> _scopedViews = views.ToDictionary(p => p.Key, p => (Control)WorkbenchStyles.CreateScope(p.Value));
    private readonly Dictionary<string, string> _documentTitles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDockable> _panels = new(StringComparer.Ordinal);
    private IRootDock? _root;
    public IReadOnlyDictionary<string, IDockable> Panels => _panels;
    /// <summary>Registers a reusable document before the workspace is mounted. Older layouts retain it as an on-demand document.</summary>
    public void RegisterDocument(string id, string title, Control view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Use an ASCII document identity of at most 100 characters.", nameof(id));
        if (string.IsNullOrWhiteSpace(title) || title.Length > 100) throw new ArgumentException("Invalid document title.", nameof(title));
        if (_scopedViews.ContainsKey(id)) throw new ArgumentException("Document identity is already registered.", nameof(id));
        var scoped = WorkbenchStyles.CreateScope(view); _scopedViews.Add(id, scoped); _documentTitles.Add(id, title);
        if (_root is null) return;
        var panel = new PanelDocument { Id = id, Title = title, View = scoped, CanClose = false, CanFloat = true };
        _panels.Add(id, panel); _root.HiddenDockables ??= CreateList<IDockable>(); _root.HiddenDockables.Add(panel);
        InitLayout(_root);
    }
    public override IRootDock CreateLayout() => CreateLayout(WorkspacePreset.Inspect);
    public IRootDock CreateLayout(WorkspacePreset preset)
    {
        _panels.Clear();
        foreach (var (id, view) in _scopedViews)
        {
            var title = id switch
            {
                "native" => "Native CLR", "styling" => "Styling", "routed" => "Routed events", "resources" => "Resource lookups", "dispatcher" => "Dispatcher queue", "xaml" => "XAML origins", "dependencies" => "Dependencies", "async" => "Async operations", "sources" => "Sources", "timeline" => "Timeline", "flame" => "Flame chart", "causality" => "Causal chain", "systems" => "Subsystems",
                "comparison" => "Compare", "visual" => "Visual tree", "logical" => "Logical tree", "properties" => "Properties & resources",
                "details" => "Event details", "events" => "Event log", "findings" => "Findings", "costs" => "Hot paths",
                "topdown" => "Call tree", "bottomup" => "Bottom-up", "statistics" => "Statistics", "threads" => "Thread activity",
                "scene" => "Layout scene", "history" => "Snapshot history", "bookmarks" => "Bookmarks", "health" => "Capture health", _ => _documentTitles.GetValueOrDefault(id, id)
            };
            IDockable panel = _documentTitles.ContainsKey(id) || id is "native" or "styling" or "resources" or "routed" or "dispatcher" or "xaml" or "dependencies" or "async" or "sources" or "timeline" or "flame" or "causality" or "systems" or "comparison" or "topdown" or "bottomup" or "statistics" or "scene" or "history"
                ? new PanelDocument { Id = id, Title = title, View = view, CanClose = false, CanFloat = true }
                : new PanelTool { Id = id, Title = title, View = view, CanClose = false, CanFloat = true, CanPin = true };
            _panels[id] = panel;
        }
        IList<IDockable> Panels(params string[] ids) => CreateList(ids.Where(_panels.ContainsKey).Select(id => _panels[id]).ToArray());
        var left = new ToolDock { Id = "left", Alignment = Alignment.Left, Proportion = preset == WorkspacePreset.Inspect ? .18 : .14,
            VisibleDockables = preset == WorkspacePreset.Native ? Panels("threads") : Panels("visual", "logical"), IsCollapsable = false };
        var right = new ToolDock { Id = "right", Alignment = Alignment.Right, Proportion = .23,
            VisibleDockables = preset == WorkspacePreset.Native ? Panels("details", "health", "bookmarks") : Panels("properties", "details", "bookmarks", "health"), IsCollapsable = false };
        var documents = new DocumentDock { Id = "documents", IsCollapsable = false, Proportion = preset is WorkspacePreset.Resources or WorkspacePreset.Styling or WorkspacePreset.Properties ? .84 : .73, CanCreateDocument = false,
            VisibleDockables = preset switch
            {
                WorkspacePreset.Native => Panels("native", "flame", "topdown", "sources"),
                WorkspacePreset.Profile => Panels("flame", "topdown", "bottomup", "statistics", "async"),
                WorkspacePreset.Compare => Panels("comparison", "timeline"),
                WorkspacePreset.Properties => Panels("property-history", "bindings", "styling", "resources", "sources"),
                WorkspacePreset.Styling => Panels("styling", "bindings", "resources", "dependencies", "sources"),
                WorkspacePreset.Resources => Panels("resources", "bindings", "dependencies", "sources", "xaml"),
                WorkspacePreset.Investigate => Panels("dispatcher", "routed", "bindings", "xaml", "dependencies", "sources"),
                _ => Panels("timeline", "scene", "causality")
            } };
        var bottom = new ToolDock { Id = "bottom", Alignment = Alignment.Bottom, Proportion = preset is WorkspacePreset.Resources or WorkspacePreset.Styling or WorkspacePreset.Properties ? .16 : .27,
            VisibleDockables = Panels("events", "findings", "costs", "threads"), IsCollapsable = false };
        left.ActiveDockable = _panels.GetValueOrDefault("visual"); right.ActiveDockable = _panels.GetValueOrDefault(preset is WorkspacePreset.Profile or WorkspacePreset.Native ? "details" : "properties");
        documents.ActiveDockable = _panels.GetValueOrDefault(preset switch { WorkspacePreset.Native => "native", WorkspacePreset.Profile => "flame", WorkspacePreset.Compare => "comparison", WorkspacePreset.Properties => "property-history", WorkspacePreset.Styling => "styling", WorkspacePreset.Resources => "resources", WorkspacePreset.Investigate => "dispatcher", _ => "timeline" });
        bottom.ActiveDockable = _panels.GetValueOrDefault(preset is WorkspacePreset.Profile or WorkspacePreset.Native ? "costs" : "findings");
        var center = new ProportionalDock { Id = "center", Orientation = Dock.Model.Core.Orientation.Vertical, Proportion = preset == WorkspacePreset.Inspect ? .59 : .63,
            VisibleDockables = CreateList<IDockable>(documents, new ProportionalDockSplitter(), bottom) };
        if (preset == WorkspacePreset.Native) { center.Proportion = .76; right.Proportion = .24; }
        var main = new ProportionalDock { Id = "main", Orientation = Dock.Model.Core.Orientation.Horizontal,
            VisibleDockables = preset == WorkspacePreset.Native ? CreateList<IDockable>(center, new ProportionalDockSplitter(), right) : CreateList<IDockable>(left, new ProportionalDockSplitter(), center, new ProportionalDockSplitter(), right) };
        var root = CreateRootDock(); root.Id = "Root"; root.IsCollapsable = false; root.ActiveDockable = main; root.DefaultDockable = main;
        root.VisibleDockables = CreateList<IDockable>(main); root.HiddenDockables = CreateList<IDockable>(_panels.Values.Where(p => !Descendants(main).Contains(p)).ToArray());
        root.LeftPinnedDockables = CreateList<IDockable>(); root.RightPinnedDockables = CreateList<IDockable>(); root.TopPinnedDockables = CreateList<IDockable>(); root.BottomPinnedDockables = CreateList<IDockable>();
        return root;
    }
    private static IHostWindow CreateManagedHost() => new ManagedHostWindow();
    private static IHostWindow CreateNativeHost()
    { var host = new HostWindow { Title = "Diagnostics Visualizer" }; WorkbenchStyles.Decorate(host); return host; }
    public override void InitLayout(IDockable layout)
    {
        _root = layout as IRootDock;
        ContextLocator = _scopedViews.ToDictionary(pair => pair.Key, pair => (Func<object?>)(() => pair.Value));
        DockableLocator = _panels.ToDictionary(pair => pair.Key, pair => (Func<IDockable?>)(() => pair.Value)); DockableLocator["Root"] = () => layout;
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>> { [nameof(IDockWindow)] = () => DockSettings.UseManagedWindows ? CreateManagedHost() : CreateNativeHost() };
        base.InitLayout(layout);
    }
    public override void FloatDockable(IDockable dockable)
    { if (_root?.HiddenDockables?.Contains(dockable) == true) Activate(dockable.Id); base.FloatDockable(dockable); }
    public void Activate(string id)
    {
        if (!_panels.TryGetValue(id, out var panel) || _root is null) return;
        if (_root.HiddenDockables?.Remove(panel) == true)
        {
            var target = Descendants(_root).OfType<IDock>().FirstOrDefault(d => panel is PanelDocument ? d is IDocumentDock : d is IToolDock t && t.Alignment == Alignment.Bottom)
                ?? Descendants(_root).OfType<IDock>().FirstOrDefault(d => panel is PanelDocument ? d is IDocumentDock : d is IToolDock);
            if (target is null) { _root.HiddenDockables.Add(panel); return; } AddDockable(target, panel);
        }
        if (IsDockablePinned(panel, _root)) { PreviewPinnedDockable(panel); return; }
        if (panel.Owner is not IDock dock) return; SetActiveDockable(panel); SetFocusedDockable(dock, panel);
    }
    public static IEnumerable<IDockable> Descendants(IDockable root)
    {
        var stack = new Stack<IDockable>(); stack.Push(root); var seen = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var node = stack.Pop(); if (!seen.Add(node)) continue; yield return node;
            if (node is IDock dock) foreach (var child in dock.VisibleDockables ?? []) stack.Push(child);
            if (node is IRootDock r) foreach (var window in r.Windows ?? []) if (window.Layout is { } layout) stack.Push(layout);
        }
    }
}
