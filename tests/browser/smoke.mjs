import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import assert from 'node:assert/strict';

await mkdir('artifacts/browser', { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 });
const errors = [], logs = [];
page.on('pageerror', error => errors.push(String(error)));
page.on('console', message => logs.push(`${message.type()}: ${message.text()}`));
page.on('response', response => { if (response.status() >= 400) errors.push(`HTTP ${response.status()} ${response.url()}`); });
try {
  await page.goto(process.env.VIEWER_URL ?? 'http://127.0.0.1:8080/', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.documentElement.dataset.viewerReady === 'true' || document.documentElement.dataset.viewerFailed === 'true', { timeout: 180_000 });
  const fatal = await page.locator('#fatal').textContent();
  assert.equal(await page.getAttribute('html', 'data-viewer-failed'), null, fatal ?? 'Browser startup failed');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth?.().visualCount > 100, { timeout: 30_000 });
  const health = await page.evaluate(() => globalThis.getDiagnosticsVisualizerHealth());
  assert(health.events > 5000, 'The actual workbench must load the example trace.');
  assert(health.nodes >= 30 && health.findings > 0);
  assert.equal(health.error, '');
  await page.screenshot({ path: 'artifacts/browser/workbench.png', fullPage: true });

  // Acquire real canvas/keyboard focus; never bypass UI input by invoking the command directly.
  await page.mouse.click(650, 220);
  await page.keyboard.press('Control+Shift+P');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().commandPaletteOpen, { timeout: 10_000 });
  await page.screenshot({ path: 'artifacts/browser/commands.png', fullPage: true });
  await page.keyboard.type('Show Causal chain');
  await page.keyboard.press('Enter');
  await page.waitForFunction(() => {
    const health = globalThis.getDiagnosticsVisualizerHealth();
    return health.causalGraphVisible && !health.commandPaletteOpen;
  }, { timeout: 10_000 });
  await page.screenshot({ path: 'artifacts/browser/causality.png', fullPage: true });
  await page.keyboard.press('Control+Shift+P');
  await page.waitForFunction(() => globalThis.getDiagnosticsVisualizerHealth().commandPaletteOpen);
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !globalThis.getDiagnosticsVisualizerHealth().commandPaletteOpen);
  await page.setViewportSize({ width: 1100, height: 760 });
  await page.waitForTimeout(250);
  await page.screenshot({ path: 'artifacts/browser/compact.png', fullPage: true });
  const finalHealth = await page.evaluate(() => globalThis.getDiagnosticsVisualizerHealth());
  assert.equal(finalHealth.error, '');
  await writeFile('artifacts/browser/health.json', JSON.stringify({ initial: health, final: finalHealth, keyboardCommands: true }, null, 2));
  assert.deepEqual(errors, [], 'Browser errors or missing assets were detected.');
  console.log(JSON.stringify({ status: 'passed', ...finalHealth }));
} catch (error) {
  await page.screenshot({ path: 'artifacts/browser/failure.png', fullPage: true }).catch(() => {});
  await writeFile('artifacts/browser/failure.txt', String(error));
  throw error;
} finally {
  await writeFile('artifacts/browser/console.log', logs.join('\n'));
  await writeFile('artifacts/browser/errors.json', JSON.stringify(errors, null, 2));
  await browser.close();
}
