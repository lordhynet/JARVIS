const { test, expect } = require('@playwright/test');
const key = 'browser-test-only-key';
test('AI feedback requires consent and renders provider text without executing markup', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Technical practice', exact: true }).click();
  const panel = page.locator('[data-panel=training]');
  await panel.getByLabel('Your technical answer').fill('I await the I/O operation.');
  const saved = page.waitForResponse(response => response.url().endsWith('/api/training/attempts') && response.request().method() === 'POST');
  await panel.getByRole('button', { name: 'Save technical answer' }).click(); const attempt = await (await saved).json();
  const feedback = panel.getByRole('button', { name: 'Request AI feedback', exact: true }); await expect(feedback).toBeDisabled();
  let calls = 0;
  await page.route('**/api/coaching/ai-status', route => route.fulfill({ json: { ready: true, model: 'fixture', detail: 'Fixture only' } }));
  await page.route('**/api/training/attempts/*/ai-assessments', async route => {
    calls++; const request = route.request().postDataJSON(); expect(request.allowCloudProcessing).toBe(true); expect(request.requestId).toBeTruthy();
    await route.fulfill({ json: { ...attempt, revision: attempt.revision + 1, canonicalAssessment: {
      source: 'ai:fixture', contentScore: 2, scores: [{ dimension: 'TechnicalAccuracy', value: 2, evidence: 'I await', rationale: '<img src=x onerror=alert(1)>' }]
    } } });
  });
  await panel.getByLabel('Allow this saved answer to be sent for paid AI feedback').check(); await feedback.click();
  await expect(panel).toContainText('Current ai:fixture score: 2 / 4');
  await expect(panel).toContainText('<img src=x onerror=alert(1)>'); await expect(panel.locator('img')).toHaveCount(0);
  expect(calls).toBe(1); await expect(panel.getByRole('button', { name: 'Request AI feedback', exact: true })).toBeDisabled();
});
async function connect(page) {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill(key); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await expect(page.locator('#workspace')).toBeVisible();
}
test('AI workflows show voice mode estimates and actual model usage', async ({ page }) => {
  await page.route('**/api/voice/modes', route => route.fulfill({ json: { modes: [
    { mode: 'Everyday', pricingReady: true, model: 'gpt-realtime-2.1-mini', maximumGbpForTenMinutes: 1.12 },
    { mode: 'Serious', pricingReady: false, detail: 'Serious voice is disabled.' }
  ] } }));
  await page.route('**/api/budget/status', route => route.fulfill({ json: { voiceUsage: [
    { mode: 'Everyday', model: 'gpt-realtime-2.1-mini', reservedGbp: 0.60, accountedGbp: 0.03 }
  ] } }));
  await connect(page);
  await page.getByRole('button', { name: 'AI workflows', exact: true }).click();
  const card = page.locator('[data-panel=workflows] .list-item').filter({ has: page.getByRole('heading', { name: 'Live voice budget' }) });
  await expect(card).toContainText('Everyday · gpt-realtime-2.1-mini · up to £1.12 reserved for ten minutes');
  await expect(card).toContainText('Serious voice is disabled.');
  await expect(card).toContainText('Everyday · gpt-realtime-2.1-mini · £0.03 accounted');
});
test('shows the last FX quote and refresh failure in AI workflows', async ({ page }) => {
  await page.route('**/api/budget/status', route => route.fulfill({ json: {
    detail: 'A current quote is required.',
    conversionBasis: { fxStatus: {
      lastAcceptedQuote: { source: 'ECB reference fixture', observedAt: new Date(Date.now() - 25 * 3600000).toISOString() },
      manualQuoteIgnored: true,
      lastError: 'The ECB feed did not return a successful response.'
    } }
  } }));
  await connect(page);
  await page.getByRole('button', { name: 'AI workflows', exact: true }).click();
  const rate = page.locator('[data-panel=workflows] .list-item').filter({ has: page.getByRole('heading', { name: 'Budget exchange rate' }) });
  await expect(rate).toContainText('ECB reference fixture · observed 25 h ago');
  await expect(rate).toContainText('Last automatic refresh failed.');
  await expect(rate).toContainText('A current quote is required.');
  await expect(rate).toContainText('Core ignored the malformed manual exchange rate.');
});
test('coaching saves, corrects and reviews a canonical session without a provider key', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Coaching', exact: true }).click();
  const panel = page.locator('[data-panel=coaching]'); await panel.getByLabel('Exercise', { exact: true }).selectOption('point-first');
  await panel.getByRole('button', { name: 'Open coaching prompt' }).click();
  await panel.getByLabel('Your coaching answer').fill('Um, we basically gathered requirements and we deployed the service.');
  await panel.getByRole('button', { name: 'Save & review text' }).click();
  await expect(panel).toContainText('2 possible filler phrases'); await expect(panel).toContainText('Pace, pauses and confidence are unavailable');
  await panel.getByLabel('Your coaching answer').fill('I owned the database migration and verified the result with a recovery test.');
  await panel.getByRole('button', { name: 'Save correction' }).click(); await expect(panel).toContainText('0 possible filler phrases');
  await panel.getByRole('button', { name: 'Review current session' }).click(); await expect(panel).toContainText('1 coaching answers');
  await panel.getByText('Saved answer versions', { exact: true }).click(); await expect(panel).toContainText('Version 2: I owned');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await page.screenshot({ path: `test-results/coaching-${test.info().project.name}.png`, fullPage: true });
});
test('story editor preserves literal text and CV defence uses a real saved claim', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Story Bank', exact: true }).click();
  const panel = page.locator('[data-panel=stories]'); const title = `Real story ${test.info().project.name} ${Date.now()}`;
  await panel.getByLabel('Title', { exact: true }).fill(title); await panel.getByLabel('Company / project').fill('Real company');
  await panel.getByLabel('Exact service / feature').fill('<img src=x onerror=alert(1)>');
  await panel.getByLabel('CV claims · one per line').fill('I implemented provider timeout reconciliation.');
  await panel.getByRole('button', { name: 'Save real story' }).click(); await expect(panel).toContainText(title);
  await page.getByRole('button', { name: 'Coaching', exact: true }).click(); const coach = page.locator('[data-panel=coaching]');
  await coach.getByLabel('Exercise', { exact: true }).selectOption('cv-defence');
  await coach.getByLabel('Source story', { exact: true }).selectOption({ label: title });
  await coach.getByLabel('Saved CV claim', { exact: true }).selectOption('0');
  await coach.getByRole('button', { name: 'Open coaching prompt' }).click(); await expect(coach).toContainText('Saved claim: I implemented provider timeout reconciliation.');
  await expect(coach.locator('img')).toHaveCount(0); await expect(coach).toContainText('<img src=x onerror=alert(1)>');
});
test('technical practice saves an explicit self rating through the shared scheduler', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Technical practice', exact: true }).click(); const panel = page.locator('[data-panel=training]');
  await expect(panel.getByLabel('Technical question')).not.toBeEmpty();
  await panel.getByLabel('Your technical answer').fill('Await releases the thread while I/O is pending.');
  await panel.getByRole('button', { name: 'Save technical answer' }).click();
  await expect(panel.getByText('Self-assessment · technical accuracy')).toBeVisible(); await panel.getByLabel('Self rating', { exact: true }).selectOption('3');
  await panel.getByLabel('Exact excerpt from saved answer').fill('Await releases the thread'); await panel.getByLabel('Why this rating?').fill('Compared with the reference.');
  await panel.getByRole('button', { name: 'Save self-assessment' }).click(); await expect(panel).toContainText('Current self score: 3 / 4');
});
test('programme check-ins are explicit self reports and module status does not invent implementation', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: '12-week programme', exact: true }).click(); const panel = page.locator('[data-panel=programme]');
  const start = panel.getByRole('button', { name: 'Start programme', exact: true });
  await expect(panel.locator('input[type=date], h3')).toBeVisible();
  if (await start.isVisible()) await start.click(); await expect(panel).toContainText('Week 1 of 12');
  const check = panel.getByRole('button', { name: 'Save weekly check-in', exact: true });
  if (await check.isVisible()) { await panel.getByLabel('Weekly reflection · optional').fill('Practised one concrete answer.'); await check.click(); }
  await expect(panel).toContainText('1 weekly self check-ins');
  await page.getByRole('button', { name: 'All modules', exact: true }).click(); const modules = page.locator('[data-panel=modules]');
  await modules.getByText('Research & review specialists · Incremental', { exact: true }).click(); await expect(modules).toContainText('Pending: Claim-level source evidence');
  await page.getByRole('button', { name: 'Connections', exact: true }).click(); await page.getByRole('button', { name: 'Disconnect this tab' }).click();
  await expect(page.locator('#workspace')).toBeHidden(); expect(await page.evaluate(() => document.body.textContent.includes('Practised one concrete answer.'))).toBeFalsy();
});
test('paid browser workflows remain disabled without server readiness', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'AI workflows', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Generate paid interview brief', exact: true })).toBeDisabled();
});

test('coaching preserves its request ID after an acknowledged server write loses its response', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Coaching', exact: true }).click(); const panel = page.locator('[data-panel=coaching]');
  await panel.getByLabel('Exercise', { exact: true }).selectOption('point-first'); await panel.getByRole('button', { name: 'Open coaching prompt' }).click();
  await panel.getByLabel('Your coaching answer').fill('I owned the migration and verified its recovery behavior.');
  let sessionId; let committed = false;
  await page.route('**/api/coaching/attempts', async route => {
    if (route.request().method() === 'POST' && !committed) {
      sessionId = route.request().postDataJSON().sessionId; const response = await route.fetch(); expect(response.ok()).toBeTruthy(); committed = true; await route.abort('failed');
    } else await route.continue();
  });
  await panel.getByRole('button', { name: 'Save & review text' }).click(); await expect(page.locator('#notice')).toContainText('Failed to fetch');
  await panel.getByRole('button', { name: 'Save & review text' }).click(); await expect(panel).toContainText('Text observations');
  const response = await page.request.get(`/api/coaching/sessions/${sessionId}/attempts`, { headers: { 'X-Jarvis-Key': key } });
  expect(await response.json()).toHaveLength(1);
});
