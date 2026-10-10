import { dotnet } from './_framework/dotnet.js';

const message = document.getElementById('loading-message');
const fatal = document.getElementById('fatal');
try {
  const runtime = await dotnet.withDiagnosticTracing(false).create();
  message.textContent = 'Starting the trace workspace…';
  const config = runtime.getConfig();
  await runtime.runMain(config.mainAssemblyName, []);
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  Object.defineProperty(globalThis, 'getDiagnosticsVisualizerHealth', {
    value: () => JSON.parse(exports.DiagnosticsVisualizer.Browser.Program.GetViewerHealth()),
    writable: false, configurable: false
  });
  const health = globalThis.getDiagnosticsVisualizerHealth();
  if (!health.ready) throw new Error('The Avalonia workbench did not initialize.');
  document.getElementById('loading').hidden = true;
  document.documentElement.dataset.viewerReady = 'true';
  console.info('Diagnostics Visualizer ready', { events: health.events, nodes: health.nodes });
} catch (error) {
  message.textContent = 'The workbench could not start.';
  fatal.hidden = false;
  fatal.textContent = `${error?.name ?? 'Error'}: ${error?.message ?? error}`;
  document.documentElement.dataset.viewerFailed = 'true';
  console.error(error);
}
