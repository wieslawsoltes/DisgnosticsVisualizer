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
  await page.mouse.click(650, 65); await command('Open synthetic native example');
  await page.waitForFunction(() => { const h = globalThis.getDiagnosticsVisualizerHealth(); return h.nativeCaptureVisible && h.nativeMethods === 3 && h.nativePairs === 32; });
  assert((await health()).source.includes('DEMO')); assert.equal((await health()).nodes, 0);
  await page.screenshot({ path: 'artifacts/browser/native-capture.png', fullPage: true });
  await command('Show Sources'); await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().activeDocuments.includes('sources'));
  await command('Show Native CLR capture'); await command('Save workspace layout');
  await page.waitForFunction(() => (localStorage.getItem('diagnostics.workspace.v1') ?? '').includes('native'));
  await page.reload({ waitUntil: 'domcontentloaded' }); await ready();
  assert.equal((await health()).workspacePreset, 'Native'); assert.equal((await health()).workspaceError, null);
  // Layout persists, source data does not. Re-open the explicitly named synthetic example.
  await page.mouse.click(650, 65); await command('Open synthetic native example');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().nativePairs === 32);
  await page.setViewportSize({ width: 1100, height: 760 }); await page.waitForTimeout(250);
  await page.screenshot({ path: 'artifacts/browser/native-compact.png', fullPage: true });
  const final = await health(); assert.equal(final.error, ''); assert.deepEqual(errors, []);
  await writeFile('artifacts/browser/native-health.json', JSON.stringify({ final, persisted: true, synthetic: true }, null, 2));
  console.log('NATIVE_BROWSER_OK coverage=32 methods=3 source-navigation=passed persistence=passed errors=0');
} catch (error) {
  await page.screenshot({ path: 'artifacts/browser/native-failure.png', fullPage: true }).catch(() => {});
  await writeFile('artifacts/browser/native-failure.txt', String(error.stack ?? error)); throw error;
} finally { await writeFile('artifacts/browser/native-errors.json', JSON.stringify(errors)); await browser.close(); }
