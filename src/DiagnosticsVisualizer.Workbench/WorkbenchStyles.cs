using Avalonia;
using Avalonia.Controls;
using System.Runtime.CompilerServices;
using Dock.Avalonia.Themes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using DiagnosticsVisualizer.Workbench.Docking;

namespace DiagnosticsVisualizer.Workbench;

public static class WorkbenchStyles
{
    private static readonly ConditionalWeakTable<Application, object> Installed = new();
    // Scope the Rider palette and variant to diagnostics, including detached documents.
    // Installing the workbench must not recolor the instrumented application's own controls.
    public static ThemeVariantScope CreateScope(Control content)
    {
        var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = content };
        Decorate(scope);
        return scope;
    }
    internal static void Decorate(Control control)
    {
        control.Classes.Add("dv-workbench");
        control.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://Dock.Avalonia.Themes.Fluent/Presets/Ide/RiderDark.axaml") });
        // Document tab templates have a specific foreground token independent of the generic Rider tab palette.
        control.Resources["DockDocumentTabSelectedForegroundBrush"] = Brushes.White;
        if (control is TopLevel topLevel) topLevel.RequestedThemeVariant = ThemeVariant.Dark;
    }
    public static void Install(Application application)
    {
        if (Installed.TryGetValue(application, out _)) return;
        if (!application.Styles.OfType<FluentTheme>().Any()) application.Styles.Add(new FluentTheme { DensityStyle = DensityStyle.Compact });
        if (!application.Styles.OfType<DockFluentTheme>().Any()) application.Styles.Add(new DockFluentTheme { DensityStyle = DockDensityStyle.Compact, CacheDocumentTabContent = true });
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        application.Styles.Add(new StyleInclude(new Uri("avares://DiagnosticsVisualizer.Workbench/"))
        { Source = new Uri("avares://DiagnosticsVisualizer.Workbench/WorkbenchTheme.axaml") });
        application.DataTemplates.Add(new FuncDataTemplate<PanelDocument>((document, _) => document is null ? null : new DiagnosticsVisualizer.Controls.ControlLeaseHost(document.View)));
        application.DataTemplates.Add(new FuncDataTemplate<PanelTool>((tool, _) => tool is null ? null : new DiagnosticsVisualizer.Controls.ControlLeaseHost(tool.View)));
        Installed.Add(application, new object());
    }
}
