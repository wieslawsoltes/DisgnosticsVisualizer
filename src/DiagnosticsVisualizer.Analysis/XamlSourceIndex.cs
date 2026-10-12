using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Analysis;

public sealed record XamlObjectOrigin(TraceEvent Event, SourceSite Site, string Type, string Display)
{
    public long ObjectId => Event.ObjectId;
    public long EventId => Event.Id;
    public string Location => Site.Location;
    public string Kind => Site.Mapping == SourceMappingKind.XamlRoot ? "Root population" : "Construction";
    public string Instruction => $"IL_{Site.IlOffset:X4}";
}

/// <summary>Indexes retained, observed origins. Absence does not imply an object was not built from XAML.</summary>
public sealed class XamlSourceIndex
{
    private readonly Dictionary<long, XamlObjectOrigin> _latest = new();
    public XamlObjectOrigin[] Origins { get; }
    public XamlSourceIndex(TraceView trace)
    {
        Origins = trace.Events.Where(e => XamlObjectEvent.IsOrigin(e) && e.ObjectId != 0 &&
                trace.SourceSites.TryGetValue(e.NameId, out var site) &&
                site.Mapping == (e.Data0 == XamlObjectEvent.RootCode ? SourceMappingKind.XamlRoot : SourceMappingKind.XamlConstruction))
            .Select(e => new XamlObjectOrigin(e, trace.SourceSites[e.NameId],
                trace.Objects.GetValueOrDefault(e.ObjectId)?.Type ?? "Unknown type", trace.Objects.GetValueOrDefault(e.ObjectId)?.Display ?? $"Object #{e.ObjectId}"))
            .OrderBy(e => e.Event.Timestamp).ThenBy(e => e.EventId).ToArray();
        foreach (var origin in Origins) _latest[origin.ObjectId] = origin;
    }
    public XamlObjectOrigin? Find(long objectId) => _latest.GetValueOrDefault(objectId);
}
