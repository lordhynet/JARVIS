const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
// PF-02 browser: a goal's Forge division tag is shown and survives an ordinary edit (saves replace the whole goal).
test('goal division tag is saved, shown and kept when the goal is edited', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Goals & automation', exact: true }).click(); const panel = page.locator('[data-panel=planning]');
  const title = 'Tagged ' + randomUUID();
  await panel.getByLabel('Goal title', { exact: true }).fill(title); await panel.getByLabel('Goal context and next step').fill('Practise one system design answer.');
  await panel.getByLabel('Goal workflow', { exact: true }).selectOption('practice-reminder');
  await panel.getByLabel('Forge division (optional)', { exact: true }).selectOption('forge.tech');
  await panel.getByLabel('Next goal review').fill('2026-12-01T10:00'); await panel.getByLabel('Goal deadline').fill('2026-12-10T10:00');
  await panel.getByRole('button', { name: 'Save persistent goal', exact: true }).click();
  const row = panel.locator('.list-item').filter({ has: page.getByText(title, { exact: true }) });
  await expect(row).toContainText('Forge TECH');
  await row.getByRole('button', { name: 'Update goal progress', exact: true }).click();
  await expect(panel.getByLabel('Forge division (optional)', { exact: true })).toHaveValue('forge.tech');
  await panel.getByLabel('Goal state', { exact: true }).selectOption('Paused');
  await panel.getByRole('button', { name: 'Save persistent goal', exact: true }).click();
  await expect(row).toContainText('Paused'); await expect(row).toContainText('Forge TECH');
  await row.getByRole('button', { name: 'Delete goal context', exact: true }).click(); await expect(row).toHaveCount(0);
});
