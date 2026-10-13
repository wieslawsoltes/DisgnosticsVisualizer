using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Protocol;

internal static class GpuCaptureChecks
{
    public static async Task VerifyAsync(string path, string reportPath)
    {
        var store = new TraceStore();
        await using (var input = File.OpenRead(path))
            await TraceFile.ReadAsync(input, block => { store.Add(block); return ValueTask.CompletedTask; }, false);
        var trace = store.GetView(); var index = new GpuTimingIndex(trace);
        if (index.Rows.Length != 48 || index.Rows.Count(r => r.IsAvailable) < 40 || index.Rows.Any(r => r.Status is "Invalid markers" or "Incomplete"))
            throw new InvalidDataException("Actual Skia timestamp query coverage/outcomes did not pass.");
        if (trace.DroppedEvents != 0 || trace.ProbeFaults != 0 || trace.MissingBlocks != 0 || trace.MetadataDrops != 0)
            throw new InvalidDataException("GPU reference capture lost evidence or faulted.");
        using var output = new MemoryStream(); await TraceExport.WriteBinaryAsync(output, trace); output.Position = 0;
        var restored = new TraceStore(); await TraceFile.ReadAsync(output, block => { restored.Add(block); return ValueTask.CompletedTask; }, false);
        var copy = new GpuTimingIndex(restored.GetView());
        if (!index.Rows.SequenceEqual(copy.Rows)) throw new InvalidDataException("GPU binary re-export changed query evidence.");
        var report = new
        {
            Queries = index.Rows.Length, Available = index.Rows.Count(r => r.IsAvailable),
            Outcomes = index.Rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
            Devices = trace.GpuSites.Values.Distinct().ToArray(),
            trace.ProbeFaults, trace.DroppedEvents, trace.MetadataDrops, trace.MissingBlocks,
            BinaryRoundtrip = true, Clock = "Uncalibrated device nanoseconds, not CPU timestamps",
            PhysicalHardwareCertified = false
        };
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
