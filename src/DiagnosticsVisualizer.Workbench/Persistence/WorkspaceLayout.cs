using System.Text;
using System.Text.Json;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench.Persistence;

public enum WorkspacePreset { Inspect, Profile, Compare, Investigate, Resources, Styling, Properties, Native }
public sealed record DockNodeState
{
    public string Kind { get; init; } = "Panel";
    public string Id { get; init; } = "";
    public string? Active { get; init; }
    public double? Proportion { get; init; }
    public int Orientation { get; init; }
    public int Alignment { get; init; }
    public DockNodeState[] Children { get; init; } = [];
    public string[] Hidden { get; init; } = [];
    public string[] Left { get; init; } = [];
    public string[] Right { get; init; } = [];
    public string[] Top { get; init; } = [];
    public string[] Bottom { get; init; } = [];
    public DockWindowStateData[] Windows { get; init; } = [];
}
public sealed record DockWindowStateData(double X, double Y, double Width, double Height, DockNodeState Root);
public sealed record WorkspaceState(int Version, string Theme, WorkspacePreset Preset, DockNodeState Root);

/// <summary>
/// Whitelisted versioned layout schema. Never serializes controls, arbitrary CLR types, URLs,
/// target data or authentication. Validation completes before touching the live layout.
/// </summary>
public static class WorkspaceLayout
{
    public const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 48 };
    public static string Serialize(IRootDock root, WorkspacePreset preset)
        => JsonSerializer.Serialize(new WorkspaceState(1, "Rider Dark", preset, Capture(root, "Root")), Json);

    public static WorkspaceState Deserialize(string json, IReadOnlySet<string> panelIds)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Workspace settings exceed the supported size.");
        var state = JsonSerializer.Deserialize<WorkspaceState>(json, Json) ?? throw new InvalidDataException("Empty workspace.");
        if (state.Version != 1 || state.Theme != "Rider Dark" || !Enum.IsDefined(state.Preset)) throw new InvalidDataException("Unsupported workspace version or theme.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var panels = new HashSet<string>(StringComparer.Ordinal); var count = 0;
        Validate(state.Root, 0, true);
        return state;
        void Panel(string id)
        {
            if (id is null) throw new InvalidDataException("Null panel identity.");
            if (!panelIds.Contains(id) || !panels.Add(id)) throw new InvalidDataException($"Unknown or duplicate workspace panel: {id}.");
        }
        void Validate(DockNodeState node, int depth, bool root = false)
        {
            if (node is null || ++count > 256 || depth > 16 || node.Id is null || node.Id.Length > 100)
                throw new InvalidDataException("Invalid or excessively nested workspace.");
            if (root != (node.Kind == "Root")) throw new InvalidDataException("Unexpected workspace root.");
            if (node.Proportion is { } proportion && (!double.IsFinite(proportion) || proportion < 0 || proportion > 1))
                throw new InvalidDataException("Invalid dock proportion.");
            if (!Enum.IsDefined((Dock.Model.Core.Orientation)node.Orientation) || !Enum.IsDefined((Alignment)node.Alignment))
                throw new InvalidDataException("Invalid dock orientation or alignment.");
            if (node.Children is null || node.Windows is null || node.Hidden is null || node.Left is null || node.Right is null || node.Top is null || node.Bottom is null)
                throw new InvalidDataException("Null workspace collection.");
            if (node.Kind is not ("Panel" or "Splitter" or "Split" or "Tools" or "Documents" or "Root")) throw new InvalidDataException("Unknown dock node kind.");
            if (node.Kind == "Panel") Panel(node.Id);
            else if (node.Id.Length != 0 && !ids.Add(node.Id)) throw new InvalidDataException("Duplicate workspace dock identity.");
            if (node.Kind is "Panel" or "Splitter" && node.Children.Length != 0) throw new InvalidDataException("A leaf cannot contain docks.");
            if (node.Children.Any(child => child is null)) throw new InvalidDataException("Null workspace child.");
            if (node.Active is not null && !node.Children.Any(child => child.Id == node.Active)) throw new InvalidDataException("Active panel is not a direct child.");
            foreach (var child in node.Children) Validate(child, depth + 1);
            foreach (var id in node.Hidden.Concat(node.Left).Concat(node.Right).Concat(node.Top).Concat(node.Bottom)) Panel(id);
            if (!root && (node.Windows.Length + node.Hidden.Length + node.Left.Length + node.Right.Length + node.Top.Length + node.Bottom.Length != 0))
                throw new InvalidDataException("Only roots may contain pinned or floating panels.");
            if (node.Windows.Length > 16) throw new InvalidDataException("Too many floating windows.");
            foreach (var window in node.Windows)
            {
                if (window is null) throw new InvalidDataException("Null workspace window.");
                if (!double.IsFinite(window.X) || !double.IsFinite(window.Y) || !double.IsFinite(window.Width) || !double.IsFinite(window.Height) ||
                    Math.Abs(window.X) > 100_000 || Math.Abs(window.Y) > 100_000 || window.Width is < 100 or > 16_384 || window.Height is < 100 or > 16_384)
                    throw new InvalidDataException("Invalid floating window bounds.");
                Validate(window.Root, depth + 1, true);
            }
        }
    }
    public static IRootDock Restore(WorkspaceState state, WorkbenchDockFactory factory)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var root = (IRootDock)Build(state.Root);
        // A newer workbench may introduce panels absent from an older valid layout.
        foreach (var panel in factory.Panels.Values.Where(p => !used.Contains(p.Id))) root.HiddenDockables!.Add(panel);
        return root;
        IDockable Build(DockNodeState stateNode)
        {
            if (stateNode.Kind == "Panel") { used.Add(stateNode.Id); return factory.Panels[stateNode.Id]; }
            if (stateNode.Kind == "Splitter") return new ProportionalDockSplitter();
            IDock dock = stateNode.Kind switch
            {
                "Root" => new RootDock { IsCollapsable = false },
                "Tools" => new ToolDock { Alignment = (Alignment)stateNode.Alignment, IsCollapsable = false },
                "Documents" => new DocumentDock { IsCollapsable = false, CanCreateDocument = false },
                "Split" => new ProportionalDock { Orientation = (Dock.Model.Core.Orientation)stateNode.Orientation },
                _ => throw new InvalidDataException("Invalid dock node.")
            };
            dock.Id = stateNode.Id; dock.Proportion = stateNode.Proportion ?? double.NaN;
            dock.VisibleDockables = factory.CreateList<IDockable>(stateNode.Children.Select(Build).ToArray());
            dock.ActiveDockable = dock.VisibleDockables.FirstOrDefault(child => child.Id == stateNode.Active) ?? dock.VisibleDockables.FirstOrDefault(c => c is not ProportionalDockSplitter);
            dock.DefaultDockable = dock.ActiveDockable;
            if (dock is IRootDock r)
            {
                IList<IDockable> Panels(string[] ids) => factory.CreateList<IDockable>(ids.Select(id => { used.Add(id); return factory.Panels[id]; }).ToArray());
                r.HiddenDockables = Panels(stateNode.Hidden); r.LeftPinnedDockables = Panels(stateNode.Left); r.RightPinnedDockables = Panels(stateNode.Right);
                r.TopPinnedDockables = Panels(stateNode.Top); r.BottomPinnedDockables = Panels(stateNode.Bottom);
                r.Windows = factory.CreateList<IDockWindow>(stateNode.Windows.Select((w, i) => (IDockWindow)new Dock.Model.Mvvm.Core.DockWindow
                {
                    Id = $"{r.Id}-window-{i}", X = w.X, Y = w.Y, Width = w.Width, Height = w.Height,
                    Title = "Diagnostics Visualizer", Layout = (IRootDock)Build(w.Root)
                }).ToArray());
            }
            return dock;
        }
    }
    private static DockNodeState Capture(IDockable node, string path)
    {
        if (node is PanelDocument or PanelTool) return new() { Id = node.Id };
        if (node is ProportionalDockSplitter) return new() { Kind = "Splitter" };
        if (node is not IDock dock) throw new InvalidDataException("Unsupported dock type.");
        var children = (dock.VisibleDockables ?? []).ToArray();
        var activeIndex = Array.IndexOf(children, dock.ActiveDockable);
        var state = new DockNodeState
        {
            Kind = dock switch { IRootDock => "Root", IDocumentDock => "Documents", IToolDock => "Tools", _ => "Split" },
            Id = path, Active = activeIndex < 0 ? null : dock.ActiveDockable is PanelDocument or PanelTool ? dock.ActiveDockable.Id : $"{path}/{activeIndex}",
            Proportion = double.IsFinite(dock.Proportion) ? Math.Clamp(dock.Proportion, 0, 1) : null,
            Orientation = dock is IProportionalDock p ? (int)p.Orientation : 0,
            Alignment = dock is IToolDock t ? (int)t.Alignment : 0,
            Children = children.Select((child, i) => Capture(child, $"{path}/{i}")).ToArray()
        };
        if (dock is not IRootDock root) return state;
        string[] Ids(IList<IDockable>? panels) => (panels ?? []).Where(p => p is PanelDocument or PanelTool).Select(p => p.Id).ToArray();
        return state with
        {
            Hidden = Ids(root.HiddenDockables), Left = Ids(root.LeftPinnedDockables), Right = Ids(root.RightPinnedDockables),
            Top = Ids(root.TopPinnedDockables), Bottom = Ids(root.BottomPinnedDockables),
            Windows = (root.Windows ?? []).Where(w => w.Layout is not null).Select((w, i) =>
                new DockWindowStateData(Finite(w.X, 50), Finite(w.Y, 50), Math.Clamp(Finite(w.Width, 720), 100, 16384),
                    Math.Clamp(Finite(w.Height, 520), 100, 16384), Capture(w.Layout!, $"{path}/window{i}"))).ToArray()
        };
    }
    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
