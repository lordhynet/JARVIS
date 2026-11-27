const { test, expect } = require('@playwright/test');

for (const mode of ['unavailable', 'capacity', 'calendar']) test(`Forge Today preserves honest state: ${mode}`, async ({ page }) => {
  await page.goto('/');
  const text = await page.evaluate(async mode => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const element = document.createElement(tag); element.textContent = text; element.className = css; return element; };
    const button = (text, action) => { const element = node('button', text); element.addEventListener('click', action); return element; };
    const api = async () => {
      if (mode === 'unavailable') throw Object.assign(new Error('Synthetic unavailable response'), { status: 503 });
      return { status: mode === 'calendar' ? 'graduated' : 'active', day: 2, days: 261, phase: 'Foundation', dueReviews: 0,
        scheduled: false, top: mode === 'calendar' ? [] : [{ label: 'TECH: Synthetic work', minutes: 15, why: 'goal review' }] };
    };
    const view = initForgeToday({ api, node, button }); await view.load(); return panel.textContent;
  }, mode);
  if (mode === 'unavailable') { expect(text).toMatch(/unavailable/i); expect(text).not.toMatch(/off or has no programme/i); }
  if (mode === 'capacity') { expect(text).toContain('Top three suggestions'); expect(text).not.toMatch(/capacity.*not set/i); }
  if (mode === 'calendar') { expect(text).toMatch(/calendar.*ended/i); expect(text).not.toMatch(/Forge is complete/i); }
});

for (const clear of ['disconnect', 'erase']) test(`a delayed travel load cannot start a Forge read after ${clear}`, async ({ page }) => {
  // Capture the actual asynchronous nav handler, so the assertion waits for its complete continuation without sleeps.
  await page.addInitScript(() => {
    const add = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function(type, listener, options) {
      if (type === 'click' && this instanceof HTMLElement && this.dataset.tab === 'briefing' && typeof listener === 'function') {
        return add.call(this, type, function(...args) { const result = listener.apply(this, args); if (result?.then) window.__briefingTask = result; return result; }, options);
      }
      return add.call(this, type, listener, options);
    };
    const fetch = window.fetch; window.__forgeCleared = false; window.__lateForgeReads = 0;
    window.fetch = function(input, ...args) {
      if (String(input).endsWith('/api/forge/today') && window.__forgeCleared) window.__lateForgeReads++;
      return fetch.call(this, input, ...args);
    };
  });
  let release, received;
  const held = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { received = resolve; });
  let erased = false;
  await page.route('**/api/privacy/status', route => route.fulfill({ json: { epoch: erased ? 1 : 0, revision: erased ? 2 : 1, erased } }));
  await page.route('**/api/privacy/erase', route => {
    expect(route.request().postDataJSON().confirmation).toBe('ERASE MY CORE DATA'); erased = true;
    return route.fulfill({ json: { epoch: 1, revision: 2, erased: true } });
  });
  await page.route('**/api/travel/routes', route => route.fulfill({ json: [] }));
  await page.route('**/api/travel/lines', async route => { received(); await held; await route.fulfill({ json: [] }); });
  await page.route('**/api/forge/today', route => route.fulfill({ json: { status: 'active', day: 2, days: 261, phase: 'Foundation', dueReviews: 0,
    scheduled: false, top: [{ label: 'LATE_SENTINEL', minutes: 5, why: 'goal review' }] } }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Daily briefing', exact: true }).click(); await started;
  // Forge must load independently while Travel is still held.
  await expect(page.locator('#forge-today')).toContainText('LATE_SENTINEL');
  if (clear === 'disconnect') {
    await page.getByRole('button', { name: 'Connections', exact: true }).click(); await page.getByRole('button', { name: 'Disconnect this tab', exact: true }).click();
  } else {
    await page.getByRole('button', { name: 'Privacy & data', exact: true }).click();
    const panel = page.locator('[data-panel=privacy]'); await expect(panel).toContainText('Core active');
    await panel.getByLabel('Type ERASE MY CORE DATA', { exact: true }).fill('ERASE MY CORE DATA');
    await panel.getByRole('button', { name: 'Erase Core data', exact: true }).click();
    await expect(panel).toContainText('Core data erased');
  }
  await page.evaluate(() => { window.__forgeCleared = true; }); release();
  const result = await page.evaluate(async () => { await window.__briefingTask; return { text: document.getElementById('forge-today').textContent, late: window.__lateForgeReads }; });
  expect(result.late).toBe(0); expect(result.text).toBe('');
  if (clear === 'disconnect') await expect(page.locator('#notice')).toBeHidden();
  else await expect(page.locator('#notice')).toContainText('Core database content erased');
});

async function deferredView(page) {
  await page.goto('/');
  await page.evaluate(async () => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const element = document.createElement(tag); element.textContent = text; element.className = css; return element; };
    const button = (text, action) => { const element = node('button', text); element.addEventListener('click', action); return element; };
    const requests = [], pending = [];
    // This fixture controls Today requests; dedicated weekly fixtures control the optional follow-up read.
    const view = initForgeToday({ node, button, api: path => {
      if (path === '/forge/evening') return Promise.resolve({ tomorrow: { status: 'active', day: 3, days: 261, scheduled: false, top: [] } });
      if (path === '/forge/week') return Promise.resolve({ from: '2026-11-26', to: '2026-12-02', reviews: 0, trainingAnswers: 0, coachingAnswers: 0, practiceCoverage: null, divisions: [] });
      requests.push(path); return new Promise((resolve, reject) => pending.push({ resolve, reject }));
    } });
    window.forgeFixture = { view, panel, requests, pending, today: label => ({ status: 'active', day: 2, days: 261, phase: 'Foundation', dueReviews: 1,
      scheduled: false, top: [{ label, minutes: 5, why: 'goal review' }] }) };
  });
}

test('the latest reload wins even when an older load fails last', async ({ page }) => {
  await deferredView(page);
  const result = await page.evaluate(async () => {
    const f = window.forgeFixture, old = f.view.load(), latest = f.view.load();
    f.pending[1].resolve(f.today('LATEST')); await latest;
    f.pending[0].reject(Object.assign(new Error('old failure'), { status: 404 })); await old;
    return { text: f.panel.textContent, requests: f.requests };
  });
  expect(result.text).toContain('LATEST'); expect(result.text).not.toMatch(/off or has no programme/);
  expect(result.requests).toEqual(['/forge/today', '/forge/today']);
});

for (const outcome of ['success', 'failure']) test(`clear invalidates a pending Forge ${outcome}`, async ({ page }) => {
  await deferredView(page);
  const text = await page.evaluate(async outcome => {
    const f = window.forgeFixture, task = f.view.load(); f.view.clear();
    if (outcome === 'success') f.pending[0].resolve(f.today('STALE_PRIVATE_CONTENT'));
    else f.pending[0].reject(new Error('STALE_PRIVATE_ERROR'));
    await task; return f.panel.textContent;
  }, outcome);
  expect(text).toBe('');
});

test('failed reload removes stale suggestions and can retry without HTML interpretation', async ({ page }) => {
  await deferredView(page);
  const result = await page.evaluate(async () => {
    const f = window.forgeFixture;
    let task = f.view.load(); f.pending[0].resolve(f.today('OLD_PRIVATE_CONTENT')); await task;
    task = f.view.load(); f.pending[1].reject(new Error('PRIVATE_ERROR_DETAIL')); await task;
    const failed = f.panel.textContent;
    f.panel.querySelector('button').click(); f.pending[2].resolve(f.today('<img src=x onerror="window.forgeInjected=true">'));
    // Wait for the real module load continuation, not a timer.
    await Promise.resolve(); await Promise.resolve();
    return { failed, text: f.panel.textContent, images: f.panel.querySelectorAll('img').length, injected: !!window.forgeInjected };
  });
  expect(result.failed).toMatch(/unavailable/i); expect(result.failed).not.toMatch(/OLD_PRIVATE_CONTENT|PRIVATE_ERROR_DETAIL/);
  expect(result.text).toContain('<img src=x'); expect(result.images).toBe(0); expect(result.injected).toBe(false);
});

for (const status of [404, 401, 403, 503, 'network']) test(`real app distinguishes Forge response ${status}`, async ({ page }) => {
  await page.route('**/api/forge/today', route => status === 'network' ? route.abort('failed')
    : route.fulfill({ status, json: { error: 'PRIVATE_SERVER_DETAIL' } }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Daily briefing', exact: true }).click();
  const panel = page.locator('#forge-today');
  await expect(panel).toContainText(status === 404 ? 'Forge is off or has no programme yet.' : 'Forge today is unavailable. Reload to retry.');
  await expect(panel).not.toContainText('PRIVATE_SERVER_DETAIL');
});
