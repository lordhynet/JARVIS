const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
test('another device advancing a drill preserves the local unsent stage draft', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789'); await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Technical practice', exact: true }).click(); const panel = page.locator('[data-panel=training]');
  await panel.getByLabel('Guided drill mode').selectOption('DeepDive');
  const started = page.waitForResponse(r => r.url().endsWith('/api/training/drills') && r.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Start guided drill', exact: true }).click(); const { drill } = await (await started).json();
  await panel.getByLabel('Guided answer', { exact: true }).fill('My local draft that has not been uploaded.');
  const step = drill.steps[0]; const response = await page.request.post('/api/training/attempts', { headers: { 'X-Jarvis-Key': 'browser-test-only-key' }, data: {
    requestId: randomUUID(), sessionId: drill.sessionId, questionId: step.questionId, questionVersion: step.questionVersion, mode: drill.mode, text: 'A different answer saved on another device.', drillId: drill.id, stepIndex: 0
  } }); expect(response.ok()).toBeTruthy();
  await panel.getByRole('button', { name: 'Reload guided progress', exact: true }).click();
  await expect(panel.getByLabel('Guided answer', { exact: true })).toHaveValue('');
  await panel.getByText('Preserved local draft · stage 1', { exact: true }).click(); await expect(panel).toContainText('My local draft that has not been uploaded.');
});
test('guided code review saves distinct stages and resumes canonical progress', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Technical practice', exact: true }).click();
  const panel = page.locator('[data-panel=training]');
  await expect(panel.getByLabel('Guided drill mode').locator('option')).toHaveCount(9);
  await panel.getByLabel('Guided drill mode').selectOption('CodeReview');
  await panel.getByRole('button', { name: 'Start guided drill', exact: true }).click();
  await expect(panel).toContainText('Stage 1: Review the fragment'); await expect(panel.locator('pre')).toContainText(/\.Result|Poller|Balance|Authorize/);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  const literal = 'I would identify the failing invariant. <img src=x onerror=alert(1)> is literal text.';
  await panel.getByLabel('Guided answer', { exact: true }).fill(literal);
  await panel.getByRole('button', { name: 'Save guided stage', exact: true }).click();
  await expect(panel).toContainText('Stage 2: Correct and verify'); await expect(panel.getByLabel('Guided answer', { exact: true })).toHaveValue('');
  await panel.getByRole('button', { name: 'Reload guided progress', exact: true }).click();
  await expect(panel).toContainText('1/2 stages answered or skipped');
  await panel.getByLabel('Guided answer', { exact: true }).fill('Make the action asynchronous, await the two I/O operations, and test failures and cancellation.');
  await panel.getByRole('button', { name: 'Save guided stage', exact: true }).click();
  await expect(panel).toContainText('CodeReview · Completed'); await expect(panel.getByLabel('Guided answer', { exact: true })).toHaveCount(0);
  await panel.getByText('Saved stage 1 · unrated', { exact: true }).click(); await expect(panel).toContainText(literal); await expect(panel.locator('img')).toHaveCount(0);
  await panel.getByRole('button', { name: 'Load saved guided drills', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'CodeReview · Completed · 2/2 stages', exact: true })).toBeVisible();
});
