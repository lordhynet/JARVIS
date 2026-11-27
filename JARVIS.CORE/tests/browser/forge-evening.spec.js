const { test, expect } = require('@playwright/test');

for (const mode of ['clear', 'paused', 'failure']) test(`evening view preserves ${mode}`, async ({ page }) => {
  await page.goto('/');
  const result = await page.evaluate(async mode => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const e = document.createElement(tag); e.textContent = text; e.className = css; return e; };
    const button = (text, action) => { const e = node('button', text); e.addEventListener('click', action); return e; };
    let release, started; const held = new Promise(resolve => { release = resolve; }), received = new Promise(resolve => { started = resolve; });
    let cleared = false, late = 0;
    const api = async path => {
      if (cleared) late++;
      if (path === '/forge/today') return { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] };
      if (path === '/forge/week') { started(); if (mode === 'clear') await held; return { from: '2026-11-26', to: '2026-12-02', reviews: 0, trainingAnswers: 0, coachingAnswers: 0, practiceCoverage: null, divisions: [] }; }
      if (mode === 'failure') throw new Error('PRIVATE_EVENING_ERROR');
      return { tomorrow: { status: 'paused', day: 3, days: 261, scheduled: false, top: [] } };
    };
    const view = initForgeToday({ api, node, button }), task = view.load(); await received;
    if (mode === 'clear') { view.clear(); cleared = true; release(); }
    await task; return { late, text: panel.textContent };
  }, mode);
  if (mode === 'clear') { expect(result.late).toBe(0); expect(result.text).toBe(''); }
  if (mode === 'paused') { expect(result.text).toMatch(/Tomorrow.*paused/i); }
  if (mode === 'failure') { expect(result.text).toContain('Evening review is unavailable'); expect(result.text).not.toContain('PRIVATE_EVENING_ERROR'); }
});

async function eveningFixture(page) {
  await page.goto('/');
  await page.evaluate(async () => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const e = document.createElement(tag); e.textContent = text; e.className = css; return e; };
    const button = (text, action) => { const e = node('button', text); e.addEventListener('click', action); return e; };
    const pending = [], waiters = [];
    const activity = { from: '2026-12-02', to: '2026-12-02', reviews: 3, trainingAnswers: 1, coachingAnswers: 2, practiceCoverage: null, divisions: [] };
    const view = initForgeToday({ node, button, api: path => {
      if (path === '/forge/today') return Promise.resolve({ status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] });
      if (path === '/forge/week') return Promise.resolve(activity);
      return new Promise((resolve, reject) => { const index = pending.length; pending.push({ resolve, reject }); waiters[index]?.(); });
    } });
    const wait = index => pending[index] ? Promise.resolve() : new Promise(resolve => { waiters[index] = resolve; });
    window.eveningFixture = { view, panel, pending, wait, first: view.load(), result: label => ({ today: activity, tomorrowDate: '2026-12-03',
      tomorrow: { status: 'active', day: 3, days: 261, scheduled: false, top: label ? [{ label, minutes: 5, why: 'goal review' }] : [] } }) };
    await wait(0);
  });
}

for (const status of ['not-started', 'graduated', 'active']) test(`Tomorrow reports ${status} without inventing activities`, async ({ page }) => {
  await eveningFixture(page);
  await page.evaluate(async status => { const f = window.eveningFixture; const result = f.result(); result.tomorrow.status = status; result.tomorrow.startsInDays = 2; f.pending[0].resolve(result); await f.first; }, status);
  const panel = page.locator('#forge-today'); await expect(panel).toContainText('Tomorrow · 2026-12-03');
  await expect(panel).toContainText(status === 'not-started' ? '2 days until Day 1' : status === 'graduated' ? 'Programme calendar ended' : 'No suggestions for tomorrow yet');
  await expect(panel).toContainText('Today’s recorded practice');
});

for (const outcome of ['success', 'failure']) test(`clear discards a pending evening ${outcome}`, async ({ page }) => {
  await eveningFixture(page);
  const text = await page.evaluate(async outcome => { const f = window.eveningFixture; f.view.clear();
    if (outcome === 'success') f.pending[0].resolve(f.result('STALE_PRIVATE_EVENING')); else f.pending[0].reject(new Error('STALE_PRIVATE_ERROR'));
    await f.first; return f.panel.textContent;
  }, outcome); expect(text).toBe('');
});

for (const outcome of ['success', 'failure']) test(`a newer reload wins over an older evening ${outcome}`, async ({ page }) => {
  await eveningFixture(page);
  const text = await page.evaluate(async outcome => { const f = window.eveningFixture, latest = f.view.load(); await f.wait(1);
    f.pending[1].resolve(f.result('LATEST_EVENING')); await latest;
    if (outcome === 'success') f.pending[0].resolve(f.result('STALE_PRIVATE_EVENING')); else f.pending[0].reject(new Error('STALE_PRIVATE_ERROR'));
    await f.first; return f.panel.textContent;
  }, outcome); expect(text).toContain('LATEST_EVENING'); expect(text).not.toMatch(/STALE_PRIVATE|Evening review is unavailable/);
});

test('daily counts and tomorrow labels use text and remain suggestions', async ({ page }) => {
  await eveningFixture(page);
  await page.evaluate(async () => { const f = window.eveningFixture; f.pending[0].resolve(f.result('<img src=x onerror="window.eveningInjected=true">')); await f.first; });
  const panel = page.locator('#forge-today'); await expect(panel).toContainText('3 reviews · 1 training answers · 2 coaching answers');
  await expect(panel).toContainText('suggestions only'); await expect(panel).toContainText('<img src=x'); await expect(panel.locator('img')).toHaveCount(0);
  expect(await page.evaluate(() => !!window.eveningInjected)).toBe(false);
});

for (const clear of ['disconnect', 'erase']) test(`held weekly completion cannot read evening after real ${clear}`, async ({ page }) => {
  await page.addInitScript(() => {
    const add = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function(type, listener, options) {
      if (type === 'click' && this instanceof HTMLElement && this.dataset.tab === 'briefing' && typeof listener === 'function') {
        return add.call(this, type, function(...args) { const result = listener.apply(this, args); if (result?.then) window.__briefingTask = result; return result; }, options);
      } return add.call(this, type, listener, options);
    };
    const fetch = window.fetch; window.__forgeCleared = false; window.__lateForgeReads = 0;
    window.fetch = function(input, ...args) { if (/\/api\/forge\/(today|week|evening)$/.test(String(input)) && window.__forgeCleared) window.__lateForgeReads++; return fetch.call(this, input, ...args); };
  });
  let release, received, erased = false; const held = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { received = resolve; });
  await page.route('**/api/privacy/status', route => route.fulfill({ json: { epoch: erased ? 1 : 0, revision: erased ? 2 : 1, erased } }));
  await page.route('**/api/privacy/erase', route => { expect(route.request().postDataJSON().confirmation).toBe('ERASE MY CORE DATA'); erased = true; return route.fulfill({ json: { epoch: 1, revision: 2, erased: true } }); });
  await page.route('**/api/forge/today', route => route.fulfill({ json: { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] } }));
  await page.route('**/api/forge/week', async route => { received(); await held; await route.fulfill({ json: { from: '2026-11-26', to: '2026-12-02', reviews: 0, trainingAnswers: 0, coachingAnswers: 0, practiceCoverage: null, divisions: [] } }); });
  await page.route('**/api/forge/evening', route => route.fulfill({ json: { tomorrowDate: '2026-12-03', tomorrow: { status: 'active', day: 3, days: 261, scheduled: false, top: [{ label: 'INDEPENDENT_EVENING', minutes: 5, why: 'goal review' }] } } }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Daily briefing', exact: true }).click(); await started;
  await expect(page.locator('#forge-today')).toContainText('INDEPENDENT_EVENING'); // weekly is still held
  if (clear === 'disconnect') { await page.getByRole('button', { name: 'Connections', exact: true }).click(); await page.getByRole('button', { name: 'Disconnect this tab', exact: true }).click(); }
  else { await page.getByRole('button', { name: 'Privacy & data', exact: true }).click(); const panel = page.locator('[data-panel=privacy]'); await expect(panel).toContainText('Core active');
    await panel.getByLabel('Type ERASE MY CORE DATA', { exact: true }).fill('ERASE MY CORE DATA'); await panel.getByRole('button', { name: 'Erase Core data', exact: true }).click(); await expect(panel).toContainText('Core data erased'); }
  await page.evaluate(() => { window.__forgeCleared = true; }); release();
  const result = await page.evaluate(async () => { await window.__briefingTask; return { late: window.__lateForgeReads, text: document.getElementById('forge-today').textContent }; });
  expect(result.late).toBe(0); expect(result.text).toBe('');
});
