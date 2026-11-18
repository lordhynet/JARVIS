const { test, expect } = require('@playwright/test');

test('speech delivery shows measured pace, honest pause labels and a session trend', async ({ page }) => {
  await page.route('**/api/coaching/delivery', route => route.fulfill({ json: [{ sessionId: '12345678-1111-4111-8111-111111111111', turns: 1, words: 11, timedTurns: 1, wordsPerMinute: 22,
    fillers: 1, firstPersonSingular: 2, firstPersonPlural: 1, pauseRatio: 0.2, openingScore: 2,
    trend: [{ at: '2026-11-18T12:00:00Z', words: 11, wordsPerMinute: 22, fillers: 1, openingScore: 2 }] }] }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Coaching', exact: true }).click();
  const panel = page.locator('[data-panel=coaching]'); await expect(panel.getByRole('heading', { name: 'Speech delivery', exact: true })).toBeVisible();
  await panel.getByText('Session 12345678 · 1 turns · 11 words', { exact: true }).click();
  await expect(panel).toContainText('22 words/min'); await expect(panel).toContainText('20% (quiet-audio heuristic)');
  await expect(panel).toContainText('I: 2 · We: 1'); await expect(panel).toContainText('not time between speakers');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});
