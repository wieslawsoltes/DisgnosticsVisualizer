using Avalonia;
using System.Runtime.CompilerServices;
using Dock.Avalonia.Themes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench;

public static class WorkbenchStyles
{
    private static readonly ConditionalWeakTable<Application, object> Installed = new();
    public static void Install(Application application)
    {
        if (Installed.TryGetValue(application, out _)) return;
        if (!application.Styles.OfType<FluentTheme>().Any()) application.Styles.Add(new FluentTheme { DensityStyle = DensityStyle.Compact });
        if (!application.Styles.OfType<DockFluentTheme>().Any()) application.Styles.Add(new DockFluentTheme { DensityStyle = DockDensityStyle.Compact, CacheDocumentTabContent = true });
        new DockFluentThemeManager().SwitchPreset(4);
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://DiagnosticsVisualizer.Workbench/WorkbenchTheme.axaml") });
        application.DataTemplates.Add(new FuncDataTemplate<PanelDocument>((document, _) => document is null ? null : new DiagnosticsVisualizer.Controls.ControlLeaseHost(document.View)));
        application.DataTemplates.Add(new FuncDataTemplate<PanelTool>((tool, _) => tool is null ? null : new DiagnosticsVisualizer.Controls.ControlLeaseHost(tool.View)));
        application.RequestedThemeVariant = ThemeVariant.Dark;
        Installed.Add(application, new object());
    }
}
