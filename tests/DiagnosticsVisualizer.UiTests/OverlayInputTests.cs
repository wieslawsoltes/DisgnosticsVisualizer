using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiagnosticsVisualizer.Workbench;
using DiagnosticsVisualizer.Workbench.Persistence;
using Xunit;

namespace DiagnosticsVisualizer.UiTests;

public sealed class OverlayInputTests
{
    [AvaloniaFact]
    public void FocusedPaletteIsTemplatedAndSelectAllWorksAcrossRepeatedOpenings()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        using var view = new WorkbenchView();
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs(); view.Focus();
        for (var i = 0; i < 8; i++)
        {
            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            var search = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Type a command…");
            // Logical focus before template creation loses shortcuts: the browser can then
            // interpret an unhandled Ctrl+A as raw text and insert a literal 'A'.
            if (search.IsFocused) Assert.Contains(search.GetVisualDescendants(), p => p is TextPresenter);
            Dispatcher.UIThread.RunJobs();
            Assert.True(search.IsFocused);
            Assert.Contains(search.GetVisualDescendants(), p => p is TextPresenter);
            window.KeyTextInput("discard");
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyTextInput("Show Sources");
            Assert.Equal("Show Sources", search.Text);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.IsFocused);
        }
        // Closing before deferred focus executes must not focus a detached input.
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        window.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.IsFocused);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBox>(), t => t.Watermark == "Type a command…");
        window.Close();
    }
}
