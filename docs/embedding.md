# Embedding the shared workbench

Install `DiagnosticsVisualizer.Agent.Avalonia` and `DiagnosticsVisualizer.Workbench`, and include `WorkbenchStyles` in the initialized Avalonia application. The agent, subscription and model have different lifetimes; subscriptions are asynchronously disposable.

```csharp
using DiagnosticsVisualizer.Agent.Avalonia;
using DiagnosticsVisualizer.Workbench;

await using var host = DiagnosticsHost.Start(new AgentOptions());
await using var subscription = host.Subscribe();
var model = new WorkbenchModel(loadDemo: false);
await model.AttachInProcessAsync(subscription.ReadAllAsync(),
    command => Task.FromResult(host.Execute(command)));
var view = new WorkbenchView(model);
// Mount view in a Window or existing host on the UI thread.
// Keep these leases alive for the lifetime of that view.
// Exclude its root from inspected tree checkpoints with host.Inspector.ExcludeRoot(root).
```

An out-of-process viewer avoids charging the target for drawing its own diagnostics UI. Excluding a root affects tree snapshots, not the cost of rendering that window. Use separate output directories for instrumented and normal application binaries.
