const { test, expect } = require('@playwright/test');

test('enrolled browser uses temporary owner headers, reconnects and clears credentials on disconnect', async ({ page }) => {
  const exports = [], ordinary = [];
  page.on('request', request => {
    if (request.url().endsWith('/api/privacy/export')) exports.push(request.headers());
    else if (request.method() === 'GET' && request.url().includes('/api/')) ordinary.push(request.headers());
  });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Connections', exact: true }).click();
  const access = page.locator('#browser-access');
  await access.getByRole('button', { name: 'Enroll this browser', exact: true }).click();
  await expect(access).toContainText('Enrolled browser');
  await expect(access.getByRole('button', { name: 'Enroll this browser', exact: true })).toBeDisabled();
  await access.getByRole('button', { name: 'Unlock owner access for five minutes', exact: true }).click();
  await expect(access).toContainText('Owner access until'); await expect(page.locator('#owner-key')).toHaveValue('');
  await page.getByRole('button', { name: 'Privacy & data', exact: true }).click();
  const download = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download Core data export', exact: true }).click(); await download;
  expect(exports).toHaveLength(1); expect(exports[0]['x-jarvis-owner-elevation']).toMatch(/^joe1_/); expect(exports[0]['x-jarvis-owner-key']).toBeUndefined();
  expect(ordinary.every(h => !h['x-jarvis-owner-key'] && !h['x-jarvis-owner-elevation'])).toBeTruthy();
  const storage = await page.evaluate(() => ({ local: Object.values(localStorage), session: Object.values(sessionStorage) }));
  expect(storage.session.some(value => value.startsWith('jdv1_'))).toBeTruthy();
  expect([...storage.local, ...storage.session].every(value => !value.includes('joe1_') && !value.includes('browser-test-only-owner-management'))).toBeTruthy();
  await page.getByRole('button', { name: 'Connections', exact: true }).click();
  await access.getByRole('button', { name: 'Lock owner access', exact: true }).click(); await expect(access).toContainText('Owner access locked');
  await page.reload(); await expect(page.getByLabel('API key', { exact: true })).not.toHaveValue('');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Connections', exact: true }).click();
  await expect(page.locator('#browser-access')).toContainText('Enrolled browser'); await expect(page.locator('#browser-access')).toContainText('Owner access locked');
  await page.getByRole('button', { name: 'Disconnect this tab', exact: true }).click();
  expect(await page.evaluate(() => sessionStorage.getItem('jarvis-enrolled-browser-credential'))).toBeNull();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});

test('revoking the enrolled browser disconnects it and clears its saved credential', async ({ page, request }) => {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Connections', exact: true }).click();
  await page.getByRole('button', { name: 'Enroll this browser', exact: true }).click(); await expect(page.locator('#browser-access')).toContainText('Enrolled browser');
  const id = await page.evaluate(() => localStorage.getItem('jarvis-device'));
  const revoked = await request.delete(`/api/devices/${id}/credential`, { headers: { 'X-Jarvis-Key': 'browser-test-only-key', 'X-Jarvis-Owner-Key': 'browser-test-only-owner-management-123456789' } });
  expect(revoked.ok()).toBeTruthy(); await page.getByRole('button', { name: 'Conversations', exact: true }).click();
  await expect(page.locator('#login')).toBeVisible(); expect(await page.evaluate(() => sessionStorage.getItem('jarvis-enrolled-browser-credential'))).toBeNull();
});
