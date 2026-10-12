using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class BindingEvidenceTests
{
    private static TraceEvent[] Call(long id, long timestamp, long duration, BindingOperationKind kind, long target = 0)
    {
        var facts = BindingOperationEvent.Facts(BindingValueKind.Null);
        return [new(id + 1, id, timestamp, 0, 20, target, 1, 1, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.BeginCode(kind, facts), 0),
            new(id + 2, id, timestamp, 0, 30, 40, 1, 1, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.ContextCode(kind), 0),
            new(id + 3, id, timestamp, 0, 0, 0, 1, 1, Subsystem.Bindings, TraceEventKind.Link, BindingOperationEvent.InputCode(kind), long.MinValue),
            new(id, 0, timestamp, duration, 20, 0, 1, 1, Subsystem.Bindings, TraceEventKind.Span, BindingOperationEvent.EndCode(kind, BindingOperationOutcome.Returned, facts), 0)];
    }
    private static BindingOperationIndex Index(TraceEvent[] events)
    {
        var store = new TraceStore(); store.Add(new TraceBlock { SessionId = Guid.NewGuid(), Frequency = 1000, Sequence = 1, Events = events });
        return new(store.GetView());
    }
    [Fact] public void NullAndSentinelEvidenceCannotSmuggleValueIdentities()
    {
        var events = Call(10, 0, 10, BindingOperationKind.Publish);
        var input = events.ToArray(); input[2] = input[2] with { ObjectId = 99 };
        Assert.Equal(1, Index(input).InvalidMarkers);
        foreach (var kind in new[] { BindingValueKind.Null, BindingValueKind.Unset, BindingValueKind.Unchanged, BindingValueKind.DoNothing, BindingValueKind.Unknown })
        {
            var end = events.ToArray(); end[3] = end[3] with { RelatedId = 99,
                Data0 = BindingOperationEvent.EndCode(BindingOperationKind.Publish, BindingOperationOutcome.Returned, BindingOperationEvent.Facts(kind)) };
            Assert.False(BindingOperationEvent.IsValid(end[3])); Assert.Equal(1, Index(end).InvalidMarkers);
        }
    }
    [Fact] public void AttachmentLookupUsesLatestCompletedObservationNotLatestEntry()
    {
        var events = Call(10, 0, 100, BindingOperationKind.Attach, 50)
            .Concat(Call(20, 20, 10, BindingOperationKind.Attach, 60))
            .Concat(Call(30, 40, 1, BindingOperationKind.Deliver))
            .Concat(Call(40, 101, 1, BindingOperationKind.Deliver)).ToArray();
        var index = Index(events);
        Assert.Equal(60, index.RecordedAttachmentTarget(index.Find(30)!));
        Assert.Equal(50, index.RecordedAttachmentTarget(index.Find(40)!));
    }
    [Fact] public void DemonstrationBindingsRemainExplicitValidAndIdentityConsistent()
    {
        var store = new TraceStore(); foreach (var block in DemoTraceFactory.Create()) store.Add(block);
        var trace = store.GetView(); var index = new BindingOperationIndex(trace);
        Assert.Equal(12, index.Calls.Length); Assert.Equal(0, index.InvalidMarkers);
        Assert.All(index.Calls, row => { Assert.True(row.IsComplete); Assert.StartsWith("Synthetic binding example:", row.Name); });
        Assert.Equal(trace.Events.Length, trace.Events.Select(e => e.Id).Distinct().Count());
        Assert.All(index.Calls, row => Assert.True(trace.Objects.ContainsKey(row.ExpressionId)));
        Assert.Equal(5, index.RecordedAttachmentTarget(index.Calls.Single(r => r.Kind == BindingOperationKind.Deliver && r.Timestamp == 1900)));
    }
}
