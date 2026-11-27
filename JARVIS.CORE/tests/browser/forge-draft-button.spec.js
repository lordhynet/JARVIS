const { test, expect } = require('@playwright/test');

async function draftFixture(page) {
  await page.goto('/');
  await page.evaluate(async () => {
    const { initForgeToday } = await import('/forge.js');
    document.body.replaceChildren(); const panel = document.createElement('section'); panel.dataset.panel = 'briefing'; document.body.append(panel);
    const node = (tag, text = '', css = '') => { const e = document.createElement(tag); e.textContent = text; e.className = css; return e; };
    const notices = [], pending = [], waiters = [], writes = [];
    const notice = (text, error = false) => notices.push({ text, error });
    // Match the existing app button wrapper: uncaught action errors become global notices.
    const button = (text, action) => { const e = node('button', text); e.addEventListener('click', () => {
      e.__task = (async () => { e.disabled = true; try { await action(); } catch (error) { notice(error.message, true); } finally { e.disabled = false; } })();
    }); return e; };
    const activity = { from: '2026-11-26', to: '2026-12-02', reviews: 0, trainingAnswers: 0, coachingAnswers: 0, practiceCoverage: null, divisions: [] };
    const api = async (path, body) => {
      if (path === '/forge/today') return { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] };
      if (path === '/forge/week') return activity;
      if (path === '/forge/evening') return { today: activity, tomorrow: { status: 'active', day: 3, days: 261, top: [] } };
      if (path !== '/forge/divisions/drafts') throw new Error('Unexpected fixture request');
      writes.push(body); return new Promise((resolve, reject) => { const index = pending.length; pending.push({ resolve, reject }); waiters[index]?.(); });
    };
    const view = initForgeToday({ api, node, button, notice });
    const wait = index => pending[index] ? Promise.resolve() : new Promise(resolve => { waiters[index] = resolve; });
    const result = [{ key: 'forge.tech', code: 'TECH', status: 'drafted' }, { key: 'forge.career', code: 'CAREER', status: 'existing' },
      { key: 'forge.relationships', code: 'RELATIONSHIPS', status: 'skipped-optional' }];
    window.draftFixture = { view, panel, notices, pending, writes, wait, result, button: () => [...panel.querySelectorAll('button')].find(e => e.textContent === 'Install Forge division drafts') };
    await view.load();
  });
}

for (const mode of ['late-success', 'late-failure', 'reload', 'partial']) test(`draft action handles ${mode}`, async ({ page }) => {
  await draftFixture(page);
  const result = await page.evaluate(async mode => {
    const f = window.draftFixture, old = f.button(); old.click(); const first = old.__task; await f.wait(0);
    if (mode.startsWith('late-')) { f.view.clear();
      if (mode === 'late-success') f.pending[0].resolve(f.result); else f.pending[0].reject(new Error('PRIVATE_DRAFT_ERROR'));
      await first; return { notices: f.notices, text: f.panel.textContent };
    }
    if (mode === 'reload') {
      await f.view.load(); const next = f.button(); next.click(); const writes = f.writes.length;
      for (const pending of f.pending) pending.resolve(f.result);
      await Promise.all([first, next.__task].filter(Boolean)); return { writes, disabled: next.disabled };
    }
    f.pending[0].reject(Object.assign(new Error('PRIVATE_DRAFT_ERROR'), { status: 409 })); await first;
    return { text: f.panel.textContent, notices: f.notices };
  }, mode);
  if (mode.startsWith('late-')) { expect(result.notices).toEqual([]); expect(result.text).toBe(''); }
  if (mode === 'reload') { expect(result.writes).toBe(1); expect(result.disabled).toBe(false); }
  if (mode === 'partial') { expect(result.text).toContain('may have partly completed'); expect(result.text).not.toContain('PRIVATE_DRAFT_ERROR'); expect(result.notices).toEqual([]); }
});

test('draft creation is explicit and reports existing and optional results without activation', async ({ page }) => {
  await draftFixture(page);
  const result = await page.evaluate(async () => {
    const f = window.draftFixture; await f.view.load(); const automatic = f.writes.length;
    const action = f.button(); action.click(); await f.wait(0); const disabled = action.disabled;
    f.pending[0].resolve(f.result); await action.__task;
    return { automatic, disabled, writes: f.writes, text: f.panel.textContent, notices: f.notices };
  });
  expect(result.automatic).toBe(0); expect(result.disabled).toBe(true); expect(result.writes).toEqual([{ includeOptional: false }]);
  expect(result.text).toContain('1 drafted, 1 existing, 1 optional skipped'); expect(result.text).toContain('Proactive agents');
  expect(result.text).toContain('unmet dependencies stay blocked'); expect(result.notices).toEqual([]);
});

test('an uncertain result retries only on a new click using the same Core request contract', async ({ page }) => {
  await draftFixture(page);
  const result = await page.evaluate(async () => {
    const f = window.draftFixture, action = f.button(); action.click(); await f.wait(0); f.pending[0].reject(new Error('connection lost')); await action.__task;
    const before = f.writes.length; action.click(); await f.wait(1); f.pending[1].resolve(f.result.map(row => row.status === 'drafted' ? { ...row, status: 'existing' } : row)); await action.__task;
    return { before, writes: f.writes, text: f.panel.textContent, disabled: action.disabled };
  });
  expect(result.before).toBe(1); expect(result.writes).toEqual([{ includeOptional: false }, { includeOptional: false }]);
  expect(result.text).toContain('0 drafted, 2 existing, 1 optional skipped'); expect(result.disabled).toBe(false);
});

for (const shape of ['empty', 'unknown-status', 'duplicate-key']) test(`unconfirmed ${shape} result cannot claim successful installation`, async ({ page }) => {
  await draftFixture(page);
  const result = await page.evaluate(async shape => {
    const f = window.draftFixture, action = f.button(); action.click(); await f.wait(0);
    f.pending[0].resolve(shape === 'empty' ? [] : shape === 'unknown-status' ? [{ key: 'forge.tech', status: 'PRIVATE_UNKNOWN_STATUS' }] : [f.result[0], f.result[0]]);
    await action.__task; return { text: f.panel.textContent, notices: f.notices };
  }, shape);
  expect(result.text).toContain('could not be confirmed'); expect(result.text).not.toContain('Draft results:');
  expect(result.text).not.toContain('PRIVATE_UNKNOWN_STATUS'); expect(result.notices).toEqual([]);
});

test('a detached action cannot start a draft command after clear', async ({ page }) => {
  await draftFixture(page);
  const result = await page.evaluate(async () => { const f = window.draftFixture, old = f.button(); f.view.clear(); old.click(); await old.__task; return { writes: f.writes, text: f.panel.textContent }; });
  expect(result.writes).toEqual([]); expect(result.text).toBe('');
});

for (const enrolled of [false, true]) test(`real app sends only an explicit draft command with ${enrolled ? 'enrolled' : 'legacy'} owner access`, async ({ page }) => {
  const writes = [];
  await page.route('**/api/forge/today', route => route.fulfill({ json: { status: 'active', day: 2, days: 261, dueReviews: 0, scheduled: false, top: [] } }));
  await page.route('**/api/forge/divisions/drafts', route => {
    const request = route.request(); expect(request.method()).toBe('POST'); expect(request.postDataJSON()).toEqual({ includeOptional: false });
    expect(request.headers()['x-jarvis-owner-key']).toBeUndefined(); expect(request.headers()['x-jarvis-owner-elevation']).toBeUndefined();
    expect(enrolled ? request.headers()['x-jarvis-key'].startsWith('jdv1_') : request.headers()['x-jarvis-key'] === 'browser-test-only-key').toBe(true);
    writes.push(request.postDataJSON()); return route.fulfill({ json: [{ key: 'forge.tech', code: 'TECH', status: 'drafted' }, { key: 'forge.relationships', code: 'RELATIONSHIPS', status: 'skipped-optional' }] });
  });
  const activations = []; page.on('request', request => { if (/\/api\/domains\/[^/]+\/(activate|rollback|disable)$/.test(request.url())) activations.push(request.url()); });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  if (enrolled) { await page.getByRole('button', { name: 'Connections', exact: true }).click(); await page.getByRole('button', { name: 'Enroll this browser', exact: true }).click(); await expect(page.locator('#browser-access')).toContainText('Enrolled browser'); }
  await page.getByRole('button', { name: 'Daily briefing', exact: true }).click(); const panel = page.locator('#forge-today');
  await expect(panel.getByRole('button', { name: 'Install Forge division drafts', exact: true })).toBeVisible(); expect(writes).toEqual([]);
  await panel.getByRole('button', { name: 'Install Forge division drafts', exact: true }).click(); await expect(panel.getByRole('status')).toContainText('1 drafted, 0 existing, 1 optional skipped');
  await panel.getByRole('button', { name: 'Reload Forge today', exact: true }).click(); await expect(panel.getByRole('button', { name: 'Install Forge division drafts', exact: true })).toBeVisible();
  expect(writes).toHaveLength(1); expect(activations).toEqual([]);
});
