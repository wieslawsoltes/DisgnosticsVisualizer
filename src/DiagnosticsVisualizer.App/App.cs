using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Dock.Settings;
using DiagnosticsVisualizer.Workbench;

namespace DiagnosticsVisualizer.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "Diagnostics Visualizer"; WorkbenchStyles.Install(this);
        DockSettings.UseManagedWindows = OperatingSystem.IsBrowser();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (!OperatingSystem.IsBrowser())
            DiagnosticsVisualizer.Workbench.Persistence.WorkspaceServices.Storage = new DiagnosticsVisualizer.Workbench.Persistence.FileWorkspaceStorage(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiagnosticsVisualizer", "workspace.v1.json"));
        var workbench = new WorkbenchView();
        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                var window = new Window
                {
                    Title = "Diagnostics Visualizer — Avalonia", Width = 1600, Height = 1000,
                    MinWidth = 900, MinHeight = 600, Content = workbench,
                    Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#1F2124"))
                };
                window.Closed += (_, _) => workbench.Dispose(); desktop.MainWindow = window; break;
            case ISingleViewApplicationLifetime single: single.MainView = workbench; break;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
