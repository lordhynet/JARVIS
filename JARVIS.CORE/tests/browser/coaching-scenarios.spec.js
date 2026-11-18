const { test, expect } = require('@playwright/test');

test('coaching scenarios show their personas, Everyday flow and honest rubric limits', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Coaching', exact: true }).click();
  const panel = page.locator('[data-panel=coaching]'); await expect(panel.getByRole('heading', { name: 'Coaching scenarios', exact: true })).toBeVisible();
  for (const title of ['Panel interview', 'Recruiter screen', 'Presentation rehearsal', 'Hostile Q&A rehearsal']) await expect(panel.getByText(title + ' · Everyday voice', { exact: true })).toBeVisible();
  await panel.getByText('Panel interview · Everyday voice', { exact: true }).click();
  await expect(panel).toContainText('Hiring manager'); await expect(panel).toContainText('Senior engineer'); await expect(panel).toContainText('No independent competence grade');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});
