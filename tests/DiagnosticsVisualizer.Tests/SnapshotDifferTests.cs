using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class SnapshotDifferTests
{
    private readonly Guid _session = Guid.NewGuid();
    private static NodeSnapshot Node(long id) => new(id, 0, 0, 0, "Button", "", 0, 0, 100, 30, true, "");
    [Fact] public void IndependentVisualAndLogicalMovesAreRetained()
    {
        var before = new SnapshotData([Node(1)], [], []);
        var after = before with { Nodes = [Node(1) with { VisualParent = 2, LogicalParent = 3, Width = 200 }] };
        var change = Assert.Single(SnapshotDiffer.Compare(_session, before, _session, after).Nodes);
        Assert.Equal(NodeChangeKind.VisualParent | NodeChangeKind.LogicalParent | NodeChangeKind.Geometry, change.Kind);
    }
    [Fact] public void TruncatedTreesDoNotClaimObjectRemoval()
    {
        var before = new SnapshotData([Node(1)], [], []);
        var after = SnapshotData.Empty with { Truncated = true };
        var diff = SnapshotDiffer.Compare(_session, before, _session, after);
        Assert.True(diff.Incomplete);
        Assert.Equal(NodeChangeKind.UnknownPresence, Assert.Single(diff.Nodes).Kind);
    }
    [Fact] public void SelectingAnotherObjectDoesNotDeleteProperties()
    {
        var before = new SnapshotData([Node(1)], [new(1, "Width", "Styled", "100", "Local", "Button")], [], 1);
        var after = new SnapshotData([Node(1), Node(2)], [new(2, "Width", "Styled", "200", "Local", "Button")], [], 2);
        Assert.Empty(SnapshotDiffer.Compare(_session, before, _session, after).Properties);
    }
    [Fact] public void DuplicateStyleFrameEntriesAreNotLost()
    {
        var property = new PropertySnapshot(1, "Width", "Style frame", "100", "Style", "Button");
        var before = new SnapshotData([Node(1)], [property, property], [], 1);
        var after = before with { Properties = [property, property with { Value = "200" }] };
        Assert.Single(SnapshotDiffer.Compare(_session, before, _session, after).Properties);
    }
    [Fact] public void CrossSessionObjectNumbersCannotBeCompared()
        => Assert.Throws<ArgumentException>(() => SnapshotDiffer.Compare(_session, SnapshotData.Empty, Guid.NewGuid(), SnapshotData.Empty));
}
