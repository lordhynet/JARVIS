const { test, expect } = require('@playwright/test');
// PF-09: Forge is off by default, so the Today panel shows a calm state rather than an error banner.
test('Forge Today panel shows a calm off state on the daily briefing tab', async ({ page }) => {
  const weekReads = []; page.on('request', request => { if (request.url().endsWith('/api/forge/week')) weekReads.push(request.url()); });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Daily briefing', exact: true }).click();
  const panel = page.locator('#forge-today');
  await expect(panel).toContainText('Forge today');
  await expect(panel).toContainText('Forge is off or has no programme yet.');
  await expect(panel.getByRole('button', { name: 'Install Forge division drafts', exact: true })).toHaveCount(0);
  expect(weekReads).toEqual([]);
  await expect(panel.getByRole('button', { name: 'Reload Forge today', exact: true })).toBeVisible();
  // The offline Travel panel may report TfL as unavailable; Forge itself must not raise an error banner.
  await expect(page.locator('#notice')).not.toContainText(/forge/i);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});
