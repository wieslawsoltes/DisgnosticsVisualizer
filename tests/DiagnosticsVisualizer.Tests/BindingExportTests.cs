using System.Text.Json;
using DiagnosticsVisualizer.Analysis;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Runtime;
using Xunit;

namespace DiagnosticsVisualizer.Tests;

public sealed class BindingExportTests
{
    [Fact] public async Task ChromeExportRetainsExactIdentityAndUnknownFlagsWithoutValueContent()
    {
        using var capture = Probe.Start(new() { TrackBindingOperations = true });
        var secret = "private-value-not-for-export-5927";
        using (var call = Probe.BeginBindingOperation("fixture", (int)BindingOperationKind.Deliver, this, null, null, null, secret,
            BindingOperationEvent.Facts(BindingValueKind.Value, true, 2, true, false), long.MinValue))
            call.Returned(1, secret, BindingOperationEvent.Facts(BindingValueKind.Value, true, 2, true, false));
        var store = new TraceStore(); store.Add(capture.Drain()); using var stream = new MemoryStream();
        await TraceExport.WriteChromeJsonAsync(stream, store.GetView()); var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain(secret, json); using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("traceEvents").EnumerateArray().First(e => e.GetProperty("ph").GetString() == "X").GetProperty("args");
        Assert.Equal("Deliver", entry.GetProperty("bindingOperation").GetString());
        Assert.Equal("Returned", entry.GetProperty("bindingOutcome").GetString());
        Assert.Equal("Validation error", entry.GetProperty("bindingError").GetString());
        Assert.Equal(entry.GetProperty("bindingInputId").GetString(), entry.GetProperty("bindingOutputId").GetString());
        Assert.Equal("0", entry.GetProperty("bindingTargetId").GetString()); Assert.False(entry.GetProperty("bindingCaptureIncomplete").GetBoolean());
    }
}
