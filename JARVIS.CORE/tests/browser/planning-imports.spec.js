const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
const headers = { 'X-Jarvis-Key': 'browser-test-only-key' };
async function connect(page) { await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789'); await page.getByRole('button', { name: 'Connect', exact: true }).click(); }

test('deleted archive source stays cleared when an earlier detail response arrives', async ({ page }) => {
  const id = randomUUID(), title = 'Delayed CV ' + id;
  const created = await page.request.post('/api/imports', { headers, data: { requestId: id, kind: 'cv', title, text: 'Private delayed source', confirmStorageConsent: true } });
  expect(created.ok()).toBeTruthy();
  let release, received;
  const held = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { received = resolve; });
  await page.route(`**/api/imports/${id}`, async route => {
    const response = await route.fetch(); received(); await held;
    await route.fulfill({ response });
  });
  await connect(page);
  await page.getByRole('button', { name: 'CV & interview archive', exact: true }).click();
  const panel = page.locator('[data-panel=imports]');
  const row = panel.locator('.list-item').filter({ has: page.getByRole('button', { name: title + ' · cv', exact: true }) });
  await row.getByRole('button', { name: title + ' · cv', exact: true }).click(); await started;
  await row.getByRole('button', { name: 'Delete source and derived claims', exact: true }).click();
  await expect(row).toHaveCount(0);
  const responseArrived = page.waitForResponse(response => response.url().endsWith('/api/imports/' + id) && response.request().method() === 'GET');
  release(); await responseArrived;
  // A subsequent authenticated refresh provides a browser event-loop barrier.
  await panel.getByRole('button', { name: 'Refresh imported sources', exact: true }).click();
  await expect(panel.getByLabel('Imported source text')).toHaveCount(0);
  await expect(panel.getByRole('heading', { name: title, exact: true })).toHaveCount(0);
});
test('persistent goal can be saved, updated and deleted without cloud consent', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Goals & automation', exact: true }).click(); const panel = page.locator('[data-panel=planning]');
  const title = 'Goal ' + randomUUID(); await panel.getByLabel('Goal title', { exact: true }).fill(title); await panel.getByLabel('Goal context and next step').fill('Explain one genuine project clearly.');
  await panel.getByLabel('Goal workflow', { exact: true }).selectOption('practice-reminder');
  await panel.getByLabel('Next goal review').fill('2026-12-01T10:00'); await panel.getByLabel('Goal deadline').fill('2026-12-10T10:00');
  await panel.getByRole('button', { name: 'Save persistent goal', exact: true }).click();
  const row = panel.locator('.list-item').filter({ has: page.getByText(title, { exact: true }) }); await expect(row).toContainText('Active');
  await row.getByRole('button', { name: 'Update goal progress', exact: true }).click(); await panel.getByLabel('Goal state', { exact: true }).selectOption('Completed');
  await panel.getByRole('button', { name: 'Save persistent goal', exact: true }).click(); await expect(row).toContainText('Completed');
  await row.getByRole('button', { name: 'Delete goal context', exact: true }).click(); await expect(row).toHaveCount(0);
});

test('switching archive sources rejects feedback from the previous selection', async ({ page }) => {
  const first = randomUUID(), second = randomUUID();
  for (const id of [first, second]) {
    const response = await page.request.post('/api/imports', { headers, data: { requestId: id, kind: 'cv', title: id, text: 'Source ' + id, confirmStorageConsent: true } });
    expect(response.ok()).toBeTruthy();
  }
  let release, received;
  const held = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { received = resolve; });
  await page.route(`**/api/imports/${first}/assessments`, async route => {
    received(); await held;
    await route.fulfill({ json: [{ summary: 'Feedback for previous source', followUps: [] }] });
  });
  await connect(page);
  await page.getByRole('button', { name: 'CV & interview archive', exact: true }).click();
  const panel = page.locator('[data-panel=imports]');
  await panel.getByRole('button', { name: first + ' · cv', exact: true }).click(); await started;
  await panel.getByRole('button', { name: second + ' · cv', exact: true }).click();
  await expect(panel.getByLabel('Imported source text')).toHaveValue('Source ' + second);
  const arrived = page.waitForResponse(response => response.url().endsWith(`/imports/${first}/assessments`));
  release(); await arrived;
  await panel.getByRole('button', { name: 'Refresh imported sources', exact: true }).click();
  await expect(panel).not.toContainText('Feedback for previous source');
  await expect(panel.getByLabel('Imported source text')).toHaveValue('Source ' + second);
});
test('CV import attaches an exact quote and deletion removes the derived claim', async ({ page }) => {
  const storyId = randomUUID(); const source = 'I owned the retry-safe payments workflow.';
  const storyResponse = await page.request.post('/api/stories', { headers, data: { requestId: storyId, content: { title: 'Import story ' + storyId, companyProject: 'Fixture', serviceFeature: 'Payments' } } }); expect(storyResponse.ok()).toBeTruthy();
  await connect(page); await page.getByRole('button', { name: 'CV & interview archive', exact: true }).click(); const panel = page.locator('[data-panel=imports]');
  const title = 'CV ' + randomUUID(); await panel.getByLabel('Document title', { exact: true }).fill(title); await panel.getByLabel('Source text or interview transcript', { exact: true }).fill(source);
  await panel.getByLabel('I consent to storing this document in Core', { exact: true }).check(); await panel.getByRole('button', { name: 'Import into Core', exact: true }).click();
  const row = panel.locator('.list-item').filter({ has: page.getByRole('button', { name: title + ' · cv', exact: true }) });
  await row.getByRole('button', { name: title + ' · cv', exact: true }).click();
  await panel.getByLabel('Story for source claim').selectOption(storyId);
  await panel.getByLabel('Imported source text').evaluate(el => { el.setSelectionRange(0, el.value.length); el.dispatchEvent(new Event('select')); });
  await panel.getByRole('button', { name: 'Attach selected CV claim to story', exact: true }).click(); await expect(panel.locator('blockquote')).toContainText(source);
  let story = await (await page.request.get('/api/stories/' + storyId, { headers })).json(); expect(story.content.cvClaims).toContain(source);
  await row.getByRole('button', { name: 'Delete source and derived claims', exact: true }).click(); await expect(row).toHaveCount(0);
  story = await (await page.request.get('/api/stories/' + storyId, { headers })).json(); expect(story.content.cvClaims).toHaveLength(0);
});
