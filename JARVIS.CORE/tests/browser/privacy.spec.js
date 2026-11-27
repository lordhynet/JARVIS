const { test, expect } = require('@playwright/test');
test('owner credential is attached only to administrative requests', async ({ page }) => {
  const ordinary = []; const ownerCalls = [];
  page.on('request', request => {
    if (request.url().includes('/api/')) {
      const owner = request.headers()['x-jarvis-owner-key'];
      if (request.url().endsWith('/api/privacy/export')) ownerCalls.push(owner);
      else if (request.method() === 'GET') ordinary.push(owner);
    }
  });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Privacy & data', exact: true }).click();
  await expect(page.locator('[data-panel=privacy]')).toContainText('Core active');
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Download Core data export', exact: true }).click(); await download;
  expect(ordinary.length).toBeGreaterThan(0); expect(ordinary.every(value => value === undefined)).toBeTruthy();
  expect(ownerCalls).toEqual(['browser-test-only-owner-management-123456789']);
});
test('privacy preview and export are available without destructive confirmation', async ({ page }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Privacy & data', exact: true }).click();
  const panel = page.locator('[data-panel=privacy]'); await expect(panel).toContainText('Core active');
  await expect(panel).toContainText('Automatic retention: disabled');
  await panel.getByLabel('Type ERASE MY CORE DATA', { exact: true }).fill('yes');
  await panel.getByRole('button', { name: 'Erase Core data', exact: true }).click();
  await expect(panel).toContainText('Core active');
  const download = page.waitForEvent('download'); await panel.getByRole('button', { name: 'Download Core data export', exact: true }).click();
  expect((await download).suggestedFilename()).toMatch(/^jarvis-core-.*\.json$/);
});

test('confirmed erasure clears private browser drafts and renders the reopen flow', async ({ page }) => {
  // Exercise browser state cleanup without erasing the shared test server database.
  await page.addInitScript(() => {
    const add = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function(type, listener, options) {
      if (type === 'click' && this instanceof HTMLElement && this.dataset.tab === 'sessions' && typeof listener === 'function') {
        return add.call(this, type, function(...args) {
          const result = listener.apply(this, args); if (result?.then) window.__sessionNavigation = result; return result;
        }, options);
      }
      return add.call(this, type, listener, options);
    };
  });
  let erased = false;
  await page.route('**/api/privacy/status', async route => route.fulfill({ json: {
    epoch: erased ? 1 : 0, revision: erased ? 2 : 1, erased, erasedAt: null, erasureRequestId: null
  } }));
  await page.route('**/api/privacy/erase', async route => {
    expect(route.request().postDataJSON().confirmation).toBe('ERASE MY CORE DATA'); erased = true;
    await route.fulfill({ json: { epoch: 1, revision: 2, erased: true } });
  });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.locator('#session-title').fill('Private unsaved title');
  let release;
  const held = new Promise(resolve => { release = resolve; });
  let received;
  const started = new Promise(resolve => { received = resolve; });
  await page.route('**/api/sessions', async route => {
    received(); await held;
    await route.fulfill({ json: [{ id: 'old-session', title: 'Old private response', activity: 'General', status: 'Active' }] });
  });
  await page.getByRole('button', { name: 'Conversations', exact: true }).click(); await started;
  await page.getByRole('button', { name: 'Privacy & data', exact: true }).click();
  const panel = page.locator('[data-panel=privacy]'); await expect(panel).toContainText('Core active');
  await panel.getByLabel('Type ERASE MY CORE DATA', { exact: true }).fill('ERASE MY CORE DATA');
  await panel.getByRole('button', { name: 'Erase Core data', exact: true }).click();
  await expect(panel).toContainText('Core data erased');
  await expect(panel.getByLabel('Type START A NEW CORE', { exact: true })).toBeVisible();
  release();
  await page.evaluate(async () => { await window.__sessionNavigation; });
  await expect(page.locator('#notice')).toContainText('Core database content erased');
  await expect(page.locator('#session-title')).toHaveValue('');
  await expect(page.locator('#session-list')).toBeEmpty();
});
