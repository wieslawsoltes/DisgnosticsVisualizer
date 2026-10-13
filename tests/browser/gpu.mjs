import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
await mkdir('artifacts/browser', { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1050 } });
const errors = [];
page.on('pageerror', error => errors.push(String(error)));
page.on('response', response => { if (response.status() >= 400) errors.push(`HTTP ${response.status()} ${response.url()}`); });
const health = () => page.evaluate(() => globalThis.getDiagnosticsVisualizerHealth());
async function ready() {
  await page.waitForFunction(() => document.documentElement.dataset.viewerReady === 'true' && globalThis.getDiagnosticsVisualizerHealth?.().overview.width > 1000, null, { timeout: 180000 });
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}
async function command(text) {
  await page.keyboard.press('Control+Shift+P');
  await page.waitForFunction(() => { const h = globalThis.getDiagnosticsVisualizerHealth(); return h.commandPaletteOpen && h.commandInputFocused; });
  await page.keyboard.press('Control+A'); await page.keyboard.type(text, { delay: 10 });
  await page.waitForFunction(text => globalThis.getDiagnosticsVisualizerHealth().commandQuery === text, text);
  await page.keyboard.press('Enter'); await page.waitForFunction(() => !globalThis.getDiagnosticsVisualizerHealth().commandPaletteOpen);
}
try {
  await page.goto(process.env.VIEWER_URL ?? 'http://127.0.0.1:8080/', { waitUntil: 'domcontentloaded' }); await ready();
  await page.mouse.click(650, 65); await command('Open synthetic GPU example');
  await page.waitForFunction(() => { const h = globalThis.getDiagnosticsVisualizerHealth(); return h.gpuVisible && h.gpuQueries === 12 && h.gpuAvailable === 10; });
  assert((await health()).source.includes('DEMO')); assert.equal((await health()).nodes, 0);
  await page.screenshot({ path: 'artifacts/browser/gpu-timings.png', fullPage: true });
  await command('Show Timeline'); await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().activeDocuments.includes('timeline'));
  await command('Show GPU timings'); await command('Save workspace layout');
  await page.waitForFunction(() => (localStorage.getItem('diagnostics.workspace.v1') ?? '').includes('gpu'));
  await page.reload({ waitUntil: 'domcontentloaded' }); await ready();
  assert.equal((await health()).workspacePreset, 'Gpu'); assert.equal((await health()).workspaceError, null);
  // Layout persists, source data does not. Re-open the explicitly named synthetic example.
  await page.mouse.click(650, 65); await command('Open synthetic GPU example');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().gpuAvailable === 10);
  await page.setViewportSize({ width: 1100, height: 760 }); await page.waitForTimeout(250);
  await page.screenshot({ path: 'artifacts/browser/gpu-compact.png', fullPage: true });
  const final = await health(); assert.equal(final.error, ''); assert.deepEqual(errors, []);
  await writeFile('artifacts/browser/gpu-health.json', JSON.stringify({ final, persisted: true, synthetic: true }, null, 2));
  console.log('GPU_BROWSER_OK queries=12 available=10 clock-separation=passed persistence=passed errors=0');
} catch (error) {
  await page.screenshot({ path: 'artifacts/browser/gpu-failure.png', fullPage: true }).catch(() => {});
  await writeFile('artifacts/browser/gpu-failure.txt', String(error.stack ?? error)); throw error;
} finally { await writeFile('artifacts/browser/gpu-errors.json', JSON.stringify(errors)); await browser.close(); }
