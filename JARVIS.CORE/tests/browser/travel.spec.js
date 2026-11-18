const { test, expect } = require('@playwright/test');

test('saved routes can be retried, edited, disabled and deleted without rendering markup', async ({ page }) => {
  let saved = [], failed = false, firstRequest;
  await page.route('**/api/travel/lines', r => r.fulfill({ json: [{ id: 'jubilee', name: 'Jubilee', mode: 'tube' }] }));
  await page.route('**/api/travel/routes**', async r => {
    const req = r.request();
    if (req.method() === 'GET') return r.fulfill({ json: saved });
    if (req.method() === 'DELETE') { saved = []; return r.fulfill({ json: true }); }
    const body = req.postDataJSON();
    if (!failed) { firstRequest = body.requestId; failed = true; return r.abort(); }
    if (req.method() === 'POST') expect(body.requestId).toBe(firstRequest);
    saved = [{ ...body, id: '12345678-1111-4111-8111-111111111111', revision: (saved[0]?.revision || 0) + 1, lines: [{ id: 'jubilee', name: 'Jubilee' }], stops: [] }];
    return r.fulfill({ json: saved[0] });
  });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Daily briefing', exact: true }).click();
  const card = page.locator('#travel-routes'); await expect(card.getByRole('heading', { name: 'My routes', exact: true })).toBeVisible();
  await card.getByLabel('Route name', { exact: true }).fill('<img src=x onerror=alert(1)>'); await card.getByLabel('Jubilee', { exact: true }).check();
  await card.getByRole('button', { name: 'Save route', exact: true }).click(); await expect(page.locator('#notice')).toHaveClass('error');
  await card.getByRole('button', { name: 'Save route', exact: true }).click(); await expect(card.getByRole('heading', { name: '<img src=x onerror=alert(1)>', exact: true })).toBeVisible(); expect(await card.locator('img').count()).toBe(0);
  await card.getByRole('button', { name: 'Edit route', exact: true }).click(); await card.getByLabel('Route name', { exact: true }).fill('Evening commute'); await card.getByLabel('Route enabled', { exact: true }).uncheck();
  await card.getByRole('button', { name: 'Save route', exact: true }).click(); await expect(card).toContainText('Disabled · Jubilee');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  await card.getByRole('button', { name: 'Delete route', exact: true }).click(); await expect(card.getByRole('button', { name: 'Edit route', exact: true })).toHaveCount(0);
});

test('route conflicts keep the draft and allow an explicit reload', async ({ page }) => {
  await page.route('**/api/travel/lines', r => r.fulfill({ json: [{ id: 'jubilee', name: 'Jubilee', mode: 'tube' }] }));
  await page.route('**/api/travel/routes', r => r.request().method() === 'GET' ? r.fulfill({ json: [] }) : r.fulfill({ status: 409, json: { error: 'Route changed. Reload before saving.' } }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Daily briefing', exact: true }).click();
  const card = page.locator('#travel-routes'); await card.getByLabel('Route name', { exact: true }).fill('My draft'); await card.getByLabel('Jubilee', { exact: true }).check(); await card.getByRole('button', { name: 'Save route', exact: true }).click();
  await expect(page.locator('#notice')).toContainText('Reload before saving'); await expect(card.getByLabel('Route name', { exact: true })).toHaveValue('My draft'); await card.getByRole('button', { name: 'Reload routes', exact: true }).click(); await expect(card.getByLabel('Route name', { exact: true })).toHaveValue('');
});
