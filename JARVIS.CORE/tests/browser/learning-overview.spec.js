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
  await expect(overview).toContainText('Your learning');
  await expect(overview).toContainText(/\d+ due now of \d+ cards\. Next 7 days in 24-hour groups \(overdue included first\): \d+( · \d+){6}\./);
  await expect(overview).toContainText('Databases:');
  const card = page.locator('#learning-list .list-item').filter({ hasText: prompt }).last();
  await card.getByRole('button', { name: 'Reveal answer' }).click();
  await card.getByRole('button', { name: 'Again', exact: true }).click();
  await expect(overview).toContainText(/Last 7 days: \d+ reviews, \d+% rated other than Again \(Again [1-9]/);
  await expect(overview).toContainText('Focus:');
});

async function connectLearning(page) {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Learning', exact: true }).click();
}
async function seedCard(page) {
  const prompt = 'Available card ' + randomUUID();
  const response = await page.request.post('/api/learning', { headers: { 'X-Jarvis-Key': 'browser-test-only-key' },
    data: { domain: 'Databases', prompt, answer: 'Synthetic answer' } });
  expect(response.ok()).toBeTruthy(); return prompt;
}
test('unavailable summary leaves due cards accessible', async ({ page }) => {
  const prompt = await seedCard(page);
  await page.route('**/api/learning/overview', route => route.fulfill({ status: 503, json: { error: 'Fixture unavailable' } }));
  await connectLearning(page);
  await expect(page.locator('#learning-list')).toContainText(prompt);
  await expect(page.locator('#learning-overview')).toContainText('Learning summary unavailable.');
});
test('pending summary never blocks due cards and disconnect clears the summary', async ({ page }) => {
  const prompt = await seedCard(page); let release;
  const held = new Promise(resolve => { release = resolve; });
  await page.route('**/api/learning/overview', async route => { await held; await route.fulfill({ json: {
    cards: 99, dueNow: 99, forecast: [99,0,0,0,0,0,0], reviewed7Days: 0, domains: []
  } }); });
  await connectLearning(page);
  await expect(page.locator('#learning-list')).toContainText(prompt);
  await page.locator('[data-tab=settings]').click(); await page.locator('#disconnect').click();
  release();
  await expect(page.locator('#learning-overview')).toBeEmpty();
  await expect(page.locator('#learning-list')).toBeEmpty();
});
test('a late summary cannot overwrite a newer refresh', async ({ page }) => {
  await page.addInitScript(() => {
    const original = window.fetch;
    window.fetch = async (...args) => {
      const response = await original(...args);
      if (String(args[0]).includes('/learning/overview')) {
        const read = response.json.bind(response);
        response.json = async () => { const body = await read(); setTimeout(() => { window.__overviewRead = (window.__overviewRead || 0) + 1; }, 0); return body; };
      }
      return response;
    };
  });
  let calls = 0, release, started;
  const held = new Promise(resolve => { release = resolve; });
  const first = new Promise(resolve => { started = resolve; });
  const summary = cards => ({ cards, dueNow: 0, forecast: [0,0,0,0,0,0,0], reviewed7Days: 0, domains: [] });
  await page.route('**/api/learning/overview', async route => {
    calls++; if (calls === 1) { started(); await held; await route.fulfill({ json: summary(99) }); }
    else await route.fulfill({ json: summary(2) });
  });
  await connectLearning(page); await first;
  await page.locator('#refresh-learning').click();
  await expect(page.locator('#learning-overview')).toContainText('0 due now of 2 cards');
  release(); await page.waitForFunction(() => window.__overviewRead === 2);
  await expect(page.locator('#learning-overview')).toContainText('0 due now of 2 cards');
  await expect(page.locator('#learning-overview')).not.toContainText('99 cards');
});
