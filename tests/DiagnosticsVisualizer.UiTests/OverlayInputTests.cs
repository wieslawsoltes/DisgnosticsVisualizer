using Avalonia;
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
    public void PassiveChromeClickActivatesCommandsWithoutStealingEmbeddedEditorFocusOnMount()
    {
        WorkspaceServices.Storage = new MemoryWorkspaceStorage();
        var editor = new TextBox { Text = "Host application editor" };
        using var view = new WorkbenchView();
        var layout = new Grid { RowDefinitions = new("40,*") };
        layout.Children.Add(editor); Grid.SetRow(view, 1); layout.Children.Add(view);
        var window = new Window { Width = 1600, Height = 1040, Content = layout };
        window.Show(); Dispatcher.UIThread.RunJobs(); editor.Focus();
        Dispatcher.UIThread.RunJobs(); Assert.True(editor.IsFocused);
        // Metrics text is intentionally passive, unlike a button or focusable chart.
        window.MouseDown(new Point(650, 105), MouseButton.Left);
        window.MouseUp(new Point(650, 105), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        window.KeyReleaseQwerty(PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        var search = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "Type a command…");
        Assert.True(search.IsFocused);
        window.KeyTextInput("Show Binding operations");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(view.GetVisualDescendants().OfType<DiagnosticsVisualizer.Controls.BindingOperationsControl>());
        window.Close();
    }
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
