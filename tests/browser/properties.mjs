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
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth?.().visualCount > 100, null, { timeout: 180000 });
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
  await command('Workspace: Properties');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().activeDocuments.includes('property-history') && globalThis.getDiagnosticsVisualizerHealth().propertyOperations === 8);
  await page.screenshot({ path: 'artifacts/browser/properties-invocations.png', fullPage: true });
  // Explicitly synthetic populated lifecycle data; packaged integration separately
  // exercises actual rewritten applications and preserves their runtime behavior.
  await command('Show Property history');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().propertyHistoryVisible);
  await page.screenshot({ path: 'artifacts/browser/properties-history.png', fullPage: true });
  await command('Save workspace layout');
  await page.waitForFunction(() => (localStorage.getItem('diagnostics.workspace.v1') ?? '').includes('property-history'));
  await page.reload({ waitUntil: 'domcontentloaded' }); await ready();
  assert((await health()).activeDocuments.includes('property-history'));
  assert.equal((await health()).workspaceError, null);
  await page.setViewportSize({ width: 1100, height: 760 });
  await page.waitForTimeout(250);
  await page.screenshot({ path: 'artifacts/browser/properties-restored-compact.png', fullPage: true });
  const final = await health(); assert.equal(final.error, ''); assert.deepEqual(errors, []);
  await writeFile('artifacts/browser/properties-health.json', JSON.stringify({ final, document: true, propertyHistoryCommand: true, persisted: true, syntheticLifecycleExamples: true }, null, 2));
  console.log('PROPERTIES_BROWSER_OK document=activated history=activated layout=restored errors=0');
} catch (error) {
  await page.screenshot({ path: 'artifacts/browser/properties-failure.png', fullPage: true }).catch(() => {});
  await writeFile('artifacts/browser/properties-failure-health.json', JSON.stringify(await health().catch(() => null), null, 2));
  await writeFile('artifacts/browser/properties-failure.txt', String(error.stack ?? error)); throw error;
} finally { await writeFile('artifacts/browser/properties-errors.json', JSON.stringify(errors)); await browser.close(); }
