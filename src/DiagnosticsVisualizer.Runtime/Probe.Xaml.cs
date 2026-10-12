using DiagnosticsVisualizer.Core;

namespace DiagnosticsVisualizer.Runtime;

public static partial class Probe
{
    /// <summary>Injection ABI after a successful reference-type newobj, or at compiled root population entry.</summary>
    public static void XamlObject(object? target, string site, int mapping)
    {
        if (target is null || Current is not { Recording: true, Options.TrackXamlObjects: true } ||
            mapping is not ((int)SourceMappingKind.XamlConstruction) and not ((int)SourceMappingKind.XamlRoot)) return;
        Instant(Subsystem.Xaml, site, target, data0: mapping == (int)SourceMappingKind.XamlRoot ?
            XamlObjectEvent.RootCode : XamlObjectEvent.ConstructionCode, kind: TraceEventKind.Link);
    }
}
