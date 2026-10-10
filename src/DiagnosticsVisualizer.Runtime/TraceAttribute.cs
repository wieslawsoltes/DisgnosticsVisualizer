using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

/// <summary>Optional application extension point; Avalonia itself requires no source attributes.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
public sealed class TraceAttribute(Subsystem subsystem = Subsystem.Application) : Attribute
{
    public Subsystem Subsystem { get; } = subsystem;
}
