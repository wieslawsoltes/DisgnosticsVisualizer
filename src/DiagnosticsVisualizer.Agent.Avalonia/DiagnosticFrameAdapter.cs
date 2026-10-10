using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Diagnostics;

namespace DiagnosticsVisualizer.Agent.Avalonia;

internal sealed record DiagnosticEntry(AvaloniaProperty Property, object? Value);
internal sealed record DiagnosticFrame(object? Source, string Type, bool IsActive, string Priority, DiagnosticEntry[] Values);

/// <summary>
/// The value-frame surface is marked PrivateApi by Avalonia and differs between release lines.
/// Reflection is confined to framework diagnostic DTOs on explicit inspection, never to view-model getters.
/// </summary>
internal static class DiagnosticFrameAdapter
{
    public static IEnumerable<DiagnosticFrame> GetAppliedFrames(this ValueStoreDiagnostic diagnostic)
    {
        var type = diagnostic.GetType();
        var property = type.GetProperty("AppliedFrames") ?? type.GetProperty("AppliedStyles") ?? type.GetProperty("AppliedValueFrames");
        if (property?.GetValue(diagnostic) is not IEnumerable frames)
            throw new NotSupportedException("This Avalonia binary does not expose a recognized value-frame diagnostic collection.");
        var count = 0;
        foreach (var frame in frames)
        {
            if (frame is null || ++count > 128) break;
            var source = Read(frame, "Source") ?? Read(frame, "Style");
            var frameType = Read(frame, "Type")?.ToString() ?? "Style";
            var active = Read(frame, "IsActive") as bool? ?? true;
            var priority = Read(frame, "Priority")?.ToString() ?? "Unknown";
            var values = new List<DiagnosticEntry>();
            if (Read(frame, "Values") is IEnumerable entries)
                foreach (var entry in entries)
                {
                    if (entry is null || values.Count >= 128) break;
                    if (Read(entry, "Property") is AvaloniaProperty p) values.Add(new(p, Read(entry, "Value")));
                }
            yield return new(source, frameType, active, priority, values.ToArray());
        }
    }
    private static object? Read(object instance, string name)
    {
        // Only framework-defined diagnostic records are reflected. No application-defined DTO adapters.
        if (instance.GetType().Assembly != typeof(AvaloniaObject).Assembly) return null;
        return instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
    }
}
