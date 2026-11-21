const { test, expect } = require('@playwright/test');
// Story Bank shows a deterministic readiness checklist per story (no AI); markup in story text stays literal.
test('story readiness score, next follow-up and checklist are shown for each saved story', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Story Bank', exact: true }).click();
  const panel = page.locator('[data-panel=stories]'); const title = `Readiness ${test.info().project.name} ${Date.now()}`;
  await panel.getByLabel('Title', { exact: true }).fill(title); await panel.getByLabel('Company / project').fill('Real company');
  await panel.getByLabel('Exact service / feature').fill('<img src=x onerror=alert(1)>');
  await panel.getByRole('button', { name: 'Save real story' }).click(); await expect(panel).toContainText(title);
  const card = panel.locator('.list-item').filter({ has: page.getByRole('heading', { name: title, exact: true }) });
  await expect(card).toContainText('Interview readiness 6% · draft');
  await expect(card).toContainText('next: What was the situation, and what problem had to be solved?');
  await card.getByText('Readiness checklist', { exact: true }).click();
  await expect(card).toContainText('Never invent a number');
  await expect(panel.locator('img')).toHaveCount(0);
});
