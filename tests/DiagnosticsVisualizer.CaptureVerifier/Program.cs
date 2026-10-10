using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Protocol;

if (args.Length != 2) throw new ArgumentException("Usage: CaptureVerifier <trace.avtrace> <report.json>");
var store = new TraceStore(1_000_000);
await using var stream = File.OpenRead(args[0]);
var read = await TraceFile.ReadAsync(stream, block => { store.Add(block); return ValueTask.CompletedTask; }, false);
var view = store.GetView();
var observed = view.Events.GroupBy(e => e.Subsystem).ToDictionary(g => g.Key.ToString(), g => g.Count());
var report = new
{
    read.Blocks, read.Events, read.TruncatedTail, view.DroppedEvents, view.ProbeFaults,
    view.MetadataDrops, view.MissingBlocks,
    Nodes = view.Snapshot.Nodes.Length,
    Properties = view.Snapshot.Properties.Length,
    Subsystems = observed,
    Findings = TraceAnalyzer.Analyze(view).Findings
};
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
if (read.Events == 0 || read.TruncatedTail || view.ProbeFaults != 0 || view.Snapshot.Nodes.Length == 0)
    throw new InvalidDataException("Instrumented target did not produce a valid, fault-free capture and tree checkpoint.");
foreach (var required in new[] { Subsystem.Layout, Subsystem.Properties, Subsystem.Xaml, Subsystem.RoutedEvents, Subsystem.PropertyNotifications })
    if (!observed.ContainsKey(required.ToString())) throw new InvalidDataException($"No executed probe for {required}.");
Console.WriteLine(JsonSerializer.Serialize(report));
