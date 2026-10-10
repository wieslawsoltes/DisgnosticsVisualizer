using Avalonia;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench;

public static class WorkbenchStyles
{
    public static void Install(Application application)
    {
        application.Styles.Add(new FluentTheme()); application.Styles.Add(new DockFluentTheme());
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://DiagnosticsVisualizer.Workbench/WorkbenchTheme.axaml") });
        application.DataTemplates.Add(new FuncDataTemplate<PanelDocument>((document, _) => document?.View));
        application.DataTemplates.Add(new FuncDataTemplate<PanelTool>((tool, _) => tool?.View));
        application.RequestedThemeVariant = ThemeVariant.Dark;
    }
}
