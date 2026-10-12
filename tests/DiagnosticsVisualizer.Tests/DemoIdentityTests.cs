using DiagnosticsVisualizer.Analysis;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class DemoIdentityTests
{
    [Fact] public void DemonstrationIdentitiesAreUniqueAcrossEverySubsystem()
    {
        foreach (var block in DemoTraceFactory.Create())
        {
            Assert.Equal(block.Objects.Length, block.Objects.Select(o => o.Id).Distinct().Count());
            Assert.Equal(block.Symbols.Length, block.Symbols.Select(o => o.Id).Distinct().Count());
        }
    }
}
