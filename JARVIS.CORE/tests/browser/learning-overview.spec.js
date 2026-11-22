const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
// The overview is computed by Core and rendered as text; rating a card refreshes it.
test('learning overview shows due load and updates after a review', async ({ page }) => {
  const prompt = 'Overview ' + randomUUID().slice(0, 8);
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Learning', exact: true }).click();
  await page.locator('#learning-domain').selectOption('Databases');
  await page.locator('#learning-prompt').fill(prompt); await page.locator('#learning-answer').fill('An index');
  await page.getByRole('button', { name: 'Add learning card' }).click();
  const overview = page.locator('#learning-overview');
  await expect(overview).toContainText('Your learning this week');
  await expect(overview).toContainText(/\d+ due now of \d+ cards\. Next 7 days: \d+( · \d+){6}\./);
  await expect(overview).toContainText('Databases:');
  const card = page.locator('#learning-list .list-item').filter({ hasText: prompt }).last();
  await card.getByRole('button', { name: 'Reveal answer' }).click();
  await card.getByRole('button', { name: 'Again', exact: true }).click();
  await expect(overview).toContainText(/Last 7 days: \d+ reviews, \d+% recalled \(Again [1-9]/);
  await expect(overview).toContainText('Focus:');
});
