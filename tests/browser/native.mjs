import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

await mkdir('artifacts/browser', { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
const errors = [];
page.on('pageerror', error => errors.push(String(error)));
page.on('response', response => { if (response.status() >= 400) errors.push(`HTTP ${response.status()} ${response.url()}`); });
const health = () => page.evaluate(() => globalThis.getDiagnosticsVisualizerHealth());
async function ready() {
  await page.waitForFunction(() => document.documentElement.dataset.viewerReady === 'true' && globalThis.getDiagnosticsVisualizerHealth?.().visualCount > 100 && globalThis.getDiagnosticsVisualizerHealth().overview.width > 1000, null, { timeout: 180000 });
  // A populated visual tree can precede its first arranged/input-ready browser frame.
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}
async function command(text) {
  await page.keyboard.press('Control+Shift+P');
  await page.waitForFunction(() => { const h = globalThis.getDiagnosticsVisualizerHealth(); return h.commandPaletteOpen && h.commandInputFocused; });
  await page.keyboard.press('Control+A'); await page.keyboard.type(text, { delay: 10 });
  await page.waitForFunction(text => globalThis.getDiagnosticsVisualizerHealth().commandQuery === text, text);
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => !globalThis.getDiagnosticsVisualizerHealth().commandPaletteOpen);
}
try {
  await page.goto(process.env.VIEWER_URL ?? 'http://127.0.0.1:8080/', { waitUntil: 'domcontentloaded' }); await ready();
  await page.mouse.click(650, 65);
  const chooser = page.waitForEvent('filechooser');
  await page.keyboard.press('Control+O');
  await (await chooser).setFiles('artifacts/browser/SYNTHETIC-native.dvntrace');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().nativePairedSpans === 1);
  await command('Workspace: Native');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().activeDocuments.includes('native'));
  const imported = await health();
  assert.equal(imported.nativeMethods, 1); assert.equal(imported.nativeInvalidGroups, 0);
  assert(imported.source.includes('SYNTHETIC-native'));
  await page.screenshot({ path: 'artifacts/browser/native-import.png', fullPage: true });
  await command('Save workspace layout');
  await page.reload({ waitUntil: 'domcontentloaded' }); await ready();
  const restored = await health(); assert.equal(restored.workspacePreset, 'Native');
  assert(restored.activeDocuments.includes('native')); assert.equal(restored.nativeMethods, 0, 'Trace data must not persist with the workspace');
  await page.setViewportSize({ width: 1100, height: 760 }); await page.waitForTimeout(250);
  await page.screenshot({ path: 'artifacts/browser/native-restored-compact.png', fullPage: true });
  assert.deepEqual(errors, []);
  await writeFile('artifacts/browser/native-import-health.json', JSON.stringify({ imported, restored, synthetic: true, realFilePicker: true }, null, 2));
} catch (error) {
  await page.screenshot({ path: 'artifacts/browser/native-import-failure.png', fullPage: true }).catch(() => {});
  await writeFile('artifacts/browser/native-import-failure.txt', String(error.stack ?? error)); throw error;
} finally { await writeFile('artifacts/browser/native-import-errors.json', JSON.stringify(errors)); await browser.close(); }
