using System.Text;

namespace DiagnosticsVisualizer.Workbench.Persistence;

/// <summary>Stores only workspace preferences. Capture credentials and target values never enter this store.</summary>
public interface IWorkspaceStorage
{
    string? Read();
    void Write(string state);
}

public static class WorkspaceServices
{
    public static IWorkspaceStorage Storage { get; set; } = new MemoryWorkspaceStorage();
}
public sealed class MemoryWorkspaceStorage : IWorkspaceStorage
{
    private string? _state;
    public string? Read() => _state;
    public void Write(string state) => _state = state;
}
public sealed class FileWorkspaceStorage(string path) : IWorkspaceStorage
{
    public string? Read()
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > WorkspaceLayout.MaxBytes) throw new InvalidDataException("Workspace settings exceed the supported size.");
        return File.ReadAllText(path, Encoding.UTF8);
    }
    public void Write(string state)
    {
        if (Encoding.UTF8.GetByteCount(state) > WorkspaceLayout.MaxBytes) throw new InvalidDataException("Workspace settings exceed the supported size.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, state, Encoding.UTF8); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
