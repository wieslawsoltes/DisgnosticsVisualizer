using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;

internal static class PropertyCaptureChecks
{
    public static object Verify(TraceView trace)
    {
        var index = new PropertyOperationIndex(trace);
        var targets = trace.Objects.Values.Where(o => o.Type == "DiagnosticsVisualizer.Sample.PropertySubject").Select(o => o.Id).ToHashSet();
        var rows = index.Calls.Where(r => targets.Contains(r.TargetId)).ToArray();
        void Require(bool ok, string message) { if (!ok) throw new InvalidDataException("Property capture: " + message); }
        bool Number(PropertyValue v, long n) => v.Kind == PropertyValueKind.Signed && v.Bits == n;
        Require(rows.Length > 20 && index.InvalidMarkers == 0, "missing operations or invalid groups");
        Require(rows.Any(r => r.Kind == PropertyOperationKind.Set && Number(r.NewValue, 40)), "original request before coercion");
        Require(rows.Any(r => r.Effective && Number(r.NewValue, 10)), "coerced effective notification");
        Require(rows.Any(r => r.BaseValue && Number(r.NewValue, 8)), "base notification beneath animation");
        Require(rows.Any(r => r.Effective && r.Priority == 4 && Number(r.NewValue, 7)), "optimized inherited notification");
        Require(rows.Any(r => r.Kind == PropertyOperationKind.DirectSetAndRaise && r.Status == "Changed") &&
            rows.Any(r => r.Kind == PropertyOperationKind.DirectSetAndRaise && r.Status == "Unchanged"), "direct change versus no-op");
        Require(rows.Any(r => r.PropertyKind == "Attached") && rows.Any(r => r.Kind == PropertyOperationKind.Clear) &&
            rows.Any(r => r.Kind == PropertyOperationKind.Coerce) && rows.Any(r => r.Kind == PropertyOperationKind.SetCurrent), "property API coverage");
        Require(rows.Any(r => r.Status == "Threw"), "original subscriber failure");
        var first = rows.First(r => r.Effective && Number(r.NewValue, 5));
        var nested = index.Descendants(first.Id).Any(r => r.Id != first.Id && r.Effective && Number(r.NewValue, 6));
        Require(nested, "reentrant notification chain");
        Require(index.Calls.Any(r => r.Kind == PropertyOperationKind.ValueEntry && r.ParticipantId > 0), "original value-entry identity");
        return new { Operations = index.Calls.Length, ReferenceOperations = rows.Length, EffectiveNotifications = rows.Count(r => r.Effective),
            BaseNotifications = rows.Count(r => r.BaseValue), DirectChanges = rows.Count(r => r.Status == "Changed"), DirectNoOps = rows.Count(r => r.Status == "Unchanged"),
            index.InvalidMarkers, ReentrantChain = nested };
    }
}
