const { test, expect } = require('@playwright/test');
test('paid AI and agent pause controls operate independently', async ({ page }) => {
  const headers = { 'X-Jarvis-Key': 'browser-test-only-key' };
  await page.request.put('/api/agents/controls', { headers, data: { paused: false, paidAiPaused: false, agentsPaused: false } });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill(headers['X-Jarvis-Key']);
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Proactive agents', exact: true }).click();
  const panel = page.locator('[data-panel=agents]');
  await panel.getByRole('button', { name: 'Pause paid AI', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Resume paid AI', exact: true })).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Pause agents', exact: true })).toBeVisible();
  let settings = await (await page.request.get('/api/agents/controls', { headers })).json(); expect(settings.paidAiPaused).toBe(true); expect(settings.agentsPaused).toBe(false);
  await panel.getByRole('button', { name: 'Pause agents', exact: true }).click(); await expect(panel.getByRole('button', { name: 'Resume agents', exact: true })).toBeVisible();
  await panel.getByRole('button', { name: 'Resume paid AI', exact: true }).click(); await expect(panel.getByRole('button', { name: 'Pause paid AI', exact: true })).toBeVisible();
  settings = await (await page.request.get('/api/agents/controls', { headers })).json(); expect(settings.paidAiPaused).toBe(false); expect(settings.agentsPaused).toBe(true);
  await panel.getByRole('button', { name: 'Resume agents', exact: true }).click();
});
