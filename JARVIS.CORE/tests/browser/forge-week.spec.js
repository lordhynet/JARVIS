const { test, expect } = require('@playwright/test');

for (const mode of ['failure', 'coverage']) test(`weekly view reports honest ${mode}`, async ({ page }) => {
  await page.goto('/');
  const text = await page.evaluate(async mode => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const e = document.createElement(tag); e.textContent = text; e.className = css; return e; };
    const button = (text, action) => { const e = node('button', text); e.addEventListener('click', action); return e; };
    const api = async path => {
      if (path === '/forge/today') return { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] };
      if (mode === 'failure') throw new Error('PRIVATE_WEEK_DETAIL');
      return { from: '2026-11-26', to: '2026-12-02', reviews: 1, trainingAnswers: 0, coachingAnswers: 0, coverage: 0.5,
        practiceCoverage: 0.5, divisions: [] };
    };
    await initForgeToday({ api, node, button }).load(); return panel.textContent;
  }, mode);
  if (mode === 'failure') { expect(text).toContain('Weekly summary is unavailable'); expect(text).not.toContain('PRIVATE_WEEK_DETAIL'); expect(text).toContain('Day 2'); }
  else { expect(text).toContain('practice coverage 50%'); expect(text).toContain('not a skill score'); }
});

async function pendingWeek(page) {
  await page.goto('/');
  await page.evaluate(async () => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const e = document.createElement(tag); e.textContent = text; e.className = css; return e; };
    const button = (text, action) => { const e = node('button', text); e.addEventListener('click', action); return e; };
    const pending = [], waiters = [];
    const view = initForgeToday({ node, button, api: path => {
      if (path === '/forge/today') return Promise.resolve({ status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] });
      return new Promise((resolve, reject) => { const index = pending.length; pending.push({ resolve, reject }); waiters[index]?.(); });
    } });
    const waitForWeek = index => pending[index] ? Promise.resolve() : new Promise(resolve => { waiters[index] = resolve; });
    window.weekFixture = { view, panel, pending, waitForWeek, first: view.load(), week: count => ({ from: '2026-11-26', to: '2026-12-02',
      reviews: count, trainingAnswers: 0, coachingAnswers: 0, practiceCoverage: null, divisions: [] }) };
    await waitForWeek(0);
  });
}

for (const outcome of ['success', 'failure']) test(`clear discards a pending weekly ${outcome}`, async ({ page }) => {
  await pendingWeek(page);
  const text = await page.evaluate(async outcome => {
    const f = window.weekFixture; f.view.clear();
    if (outcome === 'success') f.pending[0].resolve(f.week(999)); else f.pending[0].reject(new Error('PRIVATE_WEEK_ERROR'));
    await f.first; return f.panel.textContent;
  }, outcome);
  expect(text).toBe('');
});

for (const outcome of ['success', 'failure']) test(`newer reload discards an old weekly ${outcome}`, async ({ page }) => {
  await pendingWeek(page);
  const text = await page.evaluate(async outcome => {
    const f = window.weekFixture, latest = f.view.load(); await f.waitForWeek(1);
    f.pending[1].resolve(f.week(2)); await latest;
    if (outcome === 'success') f.pending[0].resolve(f.week(999)); else f.pending[0].reject(new Error('PRIVATE_WEEK_ERROR'));
    await f.first; return f.panel.textContent;
  }, outcome);
  expect(text).toContain('2 reviews'); expect(text).not.toContain('999 reviews'); expect(text).not.toMatch(/unavailable|PRIVATE_WEEK_ERROR/);
});

test('unclassified activity stays unknown and division labels are plain text', async ({ page }) => {
  await pendingWeek(page);
  await page.evaluate(async () => {
    const f = window.weekFixture;
    f.pending[0].resolve({ ...f.week(1), unclassifiedReviews: 1,
      divisions: [{ code: '<img src=x onerror="window.weekInjected=true">', practised: false, hasActiveGoal: true }] });
    await f.first;
  });
  const panel = page.locator('#forge-today'); await expect(panel).toContainText('practice coverage unknown');
  await expect(panel).toContainText('Unclassified provenance: 1 record'); await expect(panel).toContainText('no division credit is inferred');
  await expect(panel).toContainText('no recorded practice'); await expect(panel).toContainText('<img src=x'); await expect(panel.locator('img')).toHaveCount(0);
  expect(await page.evaluate(() => !!window.weekInjected)).toBe(false);
});

test('the real briefing tab keeps Today when the optional weekly HTTP request fails', async ({ page }) => {
  const reads = [];
  await page.route('**/api/forge/today', route => { reads.push(route.request().method()); return route.fulfill({ json: { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] } }); });
  await page.route('**/api/forge/week', route => { reads.push(route.request().method()); return route.fulfill({ status: 503, json: { error: 'PRIVATE_WEEK_ERROR' } }); });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Daily briefing', exact: true }).click();
  const panel = page.locator('#forge-today'); await expect(panel).toContainText('Weekly summary is unavailable');
  await expect(panel).toContainText('Day 2'); await expect(panel).not.toContainText('PRIVATE_WEEK_ERROR'); expect(reads).toEqual(['GET', 'GET']);
});
