using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class DependencyGraphTests
{
    private static TraceView View(SnapshotData snapshot, ObjectInfo[]? objects = null)
    {
        var store = new TraceStore(); store.Add(new() { SessionId = Guid.NewGuid(), Sequence = 1, Snapshot = snapshot, Objects = objects ?? [] });
        return store.GetView();
    }
    [Fact] public void TreesAndIncomingEdgesRemainDistinctAndCyclesAreBounded()
    {
        var snapshot = new SnapshotData([new(1, 2, 2, 2, "Button", "Go", 0, 0, 20, 20, true, "")], [],
            [new(2, 1, "Owner", "cycle"), new(1, 1, "Self", "self"), new(1, 1, "Self", "self")], 1, true);
        var graph = new ObjectDependencyGraph(View(snapshot, [new(2, "Theme", "My theme")]));
        Assert.Equal(5, graph.Edges(1).Count); Assert.Equal(4, graph.Edges(2).Count);
        Assert.Equal(2, graph.Edges(1).Single(e => e.Kind == "Owner").Other(1));
        var neighborhood = graph.Neighborhood(1); Assert.Equal(2, neighborhood.Nodes.Length); Assert.True(neighborhood.IncompleteSnapshot);
        Assert.Contains(neighborhood.Nodes, n => n.Id == 2 && n.Label == "My theme" && !n.IsTreeNode);
        Assert.Equal("Metadata not retained", graph.Node(99).Type);
        Assert.Empty(graph.Neighborhood(0).Nodes);
    }
    [Fact] public void LargeNeighborsPreserveTheTableAndDiscloseDiagramOmissions()
    {
        var snapshot = new SnapshotData([], [], Enumerable.Range(2, 100).Select(i => new RelationSnapshot(1, i, "Resource", i.ToString())).ToArray());
        var graph = new ObjectDependencyGraph(View(snapshot)); var neighborhood = graph.Neighborhood(1, 5, 3);
        Assert.Equal(100, graph.Edges(1).Count); Assert.Equal(4, neighborhood.Nodes.Length);
        Assert.Equal(3, neighborhood.Edges.Length); Assert.Equal(97, neighborhood.OmittedEdges);
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.Neighborhood(1, 0));
    }
    [Fact] public async Task SnapshotOnlyObjectMetadataSurvivesCaptureAndBinaryExport()
    {
        using var session = Probe.Start(); var target = new object(); var resource = new object();
        var owner = session.GetObjectId(target); var id = session.GetObjectId(resource);
        var snapshot = new SnapshotData([], [], [new(owner, id, "Resources", "snapshot only")], owner);
        session.SetSnapshot(snapshot); var block = session.Drain(); Assert.Empty(block.Events);
        Assert.Equal(2, block.Objects.Length);
        var view = View(snapshot, block.Objects); using var stream = new MemoryStream();
        await TraceExport.WriteBinaryAsync(stream, view); stream.Position = 0;
        var store = new TraceStore(); await TraceFile.ReadAsync(stream, b => { store.Add(b); return ValueTask.CompletedTask; });
        Assert.Equal(block.Objects.OrderBy(o => o.Id), store.GetView().Objects.Values.OrderBy(o => o.Id));
    }
    [Fact] public async Task ExportSplitsLargeSnapshotMetadataIntoBoundedFrames()
    {
        // 9,000 independent relation pairs fit the relation limit but not one metadata frame.
        var relations = Enumerable.Range(1, 9000).Select(i => new RelationSnapshot(i * 2, i * 2 + 1, "Reference", "")).ToArray();
        var objects = Enumerable.Range(2, 18000).ToDictionary(i => (long)i, i => new ObjectInfo(i, "Object", i.ToString()));
        var view = View(SnapshotData.Empty) with { Snapshot = new([], [], relations), Objects = objects };
        using var stream = new MemoryStream(); await TraceExport.WriteBinaryAsync(stream, view); stream.Position = 0;
        var store = new TraceStore(); var frames = 0;
        await TraceFile.ReadAsync(stream, block => { Assert.True(block.Objects.Length <= TraceLimits.MaxObjectsPerBlock); frames++; store.Add(block); return ValueTask.CompletedTask; });
        Assert.Equal(3, frames); Assert.Equal(objects.Count, store.GetView().Objects.Count);
    }
    [Fact] public async Task ExportPreservesObservationHistoryIncludingEmptyShutdown()
    {
        var populated = new SnapshotData([new(1, 0, 0, 0, "Window", "Main", 0, 0, 1, 1, true, "")], [], []);
        var store = new TraceStore(); var session = Guid.NewGuid();
        store.Add(new() { SessionId = session, Sequence = 1, Snapshot = populated, Events = [new(1, 0, 10, 5, 1, 0, 1, 1, Subsystem.Application)] });
        store.Add(new() { SessionId = session, Sequence = 2, Snapshot = SnapshotData.Empty });
        var view = store.GetView(); using var stream = new MemoryStream();
        await TraceExport.WriteBinaryAsync(stream, view); stream.Position = 0; var imported = new TraceStore();
        await TraceFile.ReadAsync(stream, b => { imported.Add(b); return ValueTask.CompletedTask; });
        var result = imported.GetView(); Assert.Empty(result.Snapshot.Nodes); Assert.Equal(2, result.Checkpoints.Length);
        Assert.Single(result.Checkpoints[0].Data.Nodes); Assert.Empty(result.Checkpoints[1].Data.Nodes);
        Assert.Equal(view.Events, result.Events); Assert.Equal(view.Checkpoints.Select(c => c.EventWatermark), result.Checkpoints.Select(c => c.EventWatermark));
    }
}
