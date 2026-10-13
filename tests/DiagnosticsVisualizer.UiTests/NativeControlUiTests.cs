using System.Threading.Channels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiagnosticsVisualizer.Core;
using DiagnosticsVisualizer.Workbench;
using Xunit;
namespace DiagnosticsVisualizer.UiTests;
public sealed class NativeControlUiTests
{
    [AvaloniaFact] public async Task RecordingControlsRequireAnAcknowledgedLiveCapabilityAndResetOnDisconnect()
    {
        await using var model = new WorkbenchModel(false);
        var frames = Channel.CreateUnbounded<TraceBlock>(); var session = Guid.NewGuid();
        var queries = 0; var pauses = 0;
        await model.AttachInProcessAsync(frames.Reader.ReadAllAsync(), command => {
            if (command.Kind == CommandKind.Capabilities) queries++;
            if (command.Kind == CommandKind.Pause) pauses++;
            return Task.FromResult(new TraceCommand(command.RequestId, CommandKind.Response, 0,
                command.Kind == CommandKind.Capabilities ? "NATIVE_PAUSE_RESUME" : "OK"));
        });
        await frames.Writer.WriteAsync(new TraceBlock { SessionId = session, Sequence = 1, Frequency = 1000, Recording = true,
            Symbols = [new(1000000, NativeMetadataSymbol.Encode(new NativeProcessInfo(123, 8, 3)))] });
        for (var n = 0; n < 100 && queries == 0; n++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.Equal(1, queries); Assert.True(model.SupportsRecordingControl);
        await model.ToggleRecordingAsync(); Assert.Equal(1, pauses);
        await model.DisconnectAsync(); Assert.False(model.SupportsRecordingControl);
        model.LoadNativeDemo(); Assert.False(model.SupportsRecordingControl);
    }
}
