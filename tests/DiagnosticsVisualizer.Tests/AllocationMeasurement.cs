using System.Runtime.CompilerServices;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

/// <summary>
/// Isolates exact allocation-contract intervals from collection-time counter effects. This
/// guard is test-only; target probes never suppress GC. Acquisition and release occur outside
/// counter reads. Failure to acquire the budget, or a collection during it, fails the test.
/// </summary>
internal sealed class AllocationMeasurement : IDisposable
{
    private readonly int _thread, _gen0, _gen1, _gen2;
    private bool _active;
    public AllocationMeasurement()
    {
        _thread = Environment.CurrentManagedThreadId;
        if (!GC.TryStartNoGCRegion(64 * 1024 * 1024))
            throw new InvalidOperationException("Could not acquire the test allocation-measurement budget.");
        _active = true;
        _gen0 = GC.CollectionCount(0); _gen1 = GC.CollectionCount(1); _gen2 = GC.CollectionCount(2);
    }
    public static long Measure(Action<int> body, int iterations = 10000)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        using var region = new AllocationMeasurement();
        body(256); // Fixed warm-up, never choose a favorable measured sample.
        return MeasureCore(body, iterations);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCore(Action<int> body, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        body(iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        var valid = _thread == Environment.CurrentManagedThreadId && _gen0 == GC.CollectionCount(0) &&
            _gen1 == GC.CollectionCount(1) && _gen2 == GC.CollectionCount(2);
        GC.EndNoGCRegion(); // Also fails when the budget was exceeded or somebody ended the region.
        if (!valid) throw new InvalidOperationException("The allocation measurement crossed a thread or collection boundary.");
    }
}

public sealed class AllocationMeasurementTests
{
    [Fact] public void ExactAllocationGateHasZeroAndPositiveControls()
    {
        // No printing, assertions, guard construction or reflection between the counter reads.
        _ = MeasureEmpty(); _ = MeasurePositive();
        long zero, positive;
        using (var measurement = new AllocationMeasurement()) { zero = MeasureEmpty(); positive = MeasurePositive(); }
        Assert.Equal(0, zero);
        Assert.True(positive >= 37, $"A real heap allocation was not observed: {positive} bytes.");
        Assert.Equal(0, AllocationMeasurement.Measure(static n => { for (var i = 0; i < n; i++) Thread.SpinWait(1); }));
        var actual = AllocationMeasurement.Measure(static n => { for (var i = 0; i < n; i++) GC.KeepAlive(Allocate()); }, 128);
        Assert.True(actual >= 128 * 37, $"The callback's heap allocations were not observed: {actual} bytes.");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureEmpty()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        Thread.SpinWait(10000);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasurePositive()
    {
        var before = GC.GetAllocatedBytesForCurrentThread(); var value = Allocate();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before; GC.KeepAlive(value); return allocated;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object Allocate() => new byte[37];
}
