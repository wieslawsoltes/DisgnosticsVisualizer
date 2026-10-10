using System.ComponentModel;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;

namespace DiagnosticsVisualizer.Weaver.Fixtures;

public sealed class Subject : INotifyPropertyChanged
{
    private int _value;
    public static int FinallyCount;
    public event PropertyChangedEventHandler? PropertyChanged;
    [Trace(Subsystem.Layout)]
    public int Compute(int value)
    {
        try
        {
            switch (value)
            {
                case 0: return 7;
                case 1: throw new ArgumentException("filtered");
                case 2: throw new InvalidOperationException("original exception");
                case 3: try { return value * 11; } finally { FinallyCount += 10; }
                default: return value * 2;
            }
        }
        catch (ArgumentException) when (value == 1) { return 11; }
        finally { FinallyCount++; }
    }
    [Trace(Subsystem.Application)]
    public static T Echo<T>(T value) => value;
    [Trace(Subsystem.Properties)]
    public ref int ReferenceReturn() => ref _value;
    public void Raise() => PropertyChanged?.Invoke(this, new("Value"));
}
