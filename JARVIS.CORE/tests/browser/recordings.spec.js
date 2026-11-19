const { test, expect } = require('@playwright/test');
const consent = 'I have told the interviewer, or I am otherwise entitled to record this conversation, and it is for my own practice only.';

async function connect(page, enrolled = false) {
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  if (enrolled) {
    await page.getByRole('button', { name: 'Connections', exact: true }).click();
    await page.getByRole('button', { name: 'Enroll this browser', exact: true }).click();
    await expect(page.locator('#browser-access')).toContainText('Enrolled browser');
    await page.getByRole('button', { name: 'Unlock owner access for five minutes', exact: true }).click();
    await expect(page.locator('#browser-access')).toContainText('Owner access until');
  }
}

test('recording consent, retry identity, literal titles and elevated deletion stay scoped to the owner', async ({ page }) => {
  await connect(page, true);
  const id = '12345678-1111-4111-8111-111111111111'; let saved = [], writes = [], deletions = [];
  await page.route('**/api/recordings/status', r => r.fulfill({ json: { ready: true, consent } }));
  await page.route('**/api/devices', r => r.fulfill({ json: [{ device: { registration: { deviceId: 'phone', name: 'My phone', kind: 'AndroidCompanion', capabilities: { canListen: true, extra: [] } } } }] }));
  await page.route('**/api/recordings?locked=false', r => r.fulfill({ json: saved }));
  await page.route('**/api/recordings', r => {
    const request = r.request(); const body = request.postDataJSON(); writes.push({ body, headers: request.headers() });
    if (writes.length === 1) return r.abort();
    saved = [{ ...body, id, revision: 3, status: 'Incomplete', durationSamples: 16000, audioDeleted: false }];
    return r.fulfill({ json: saved[0] });
  });
  await page.route(`**/api/recordings/${id}/delete`, r => { deletions.push({ body: r.request().postDataJSON(), headers: r.request().headers() }); saved = []; return r.fulfill({ json: true }); });
  await page.getByRole('button', { name: 'Interview recordings', exact: true }).click(); const panel = page.locator('[data-panel="recordings"]');
  await panel.getByLabel('Interview name', { exact: true }).fill('<img src=x onerror=alert(1)>');
  await panel.getByRole('button', { name: 'Prepare recording', exact: true }).click();
  await expect(page.locator('#notice')).toContainText('Confirm storage and recording authorization'); expect(writes).toHaveLength(0);
  await panel.getByLabel('I agree to store this recording for my private practice.', { exact: true }).check();
  await panel.getByLabel(consent, { exact: true }).check();
  await panel.getByRole('button', { name: 'Prepare recording', exact: true }).click(); await expect(page.locator('#notice')).toHaveClass('error');
  await panel.getByRole('button', { name: 'Prepare recording', exact: true }).click();
  await expect(panel.getByRole('heading', { name: '<img src=x onerror=alert(1)>', exact: true })).toBeVisible();
  expect(writes).toHaveLength(2); expect(writes[0].body.requestId).toBe(writes[1].body.requestId);
  expect(writes[1].body).toMatchObject({ locked: false, confirmRecordingAuthorised: true, confirmStorageConsent: true, deviceId: 'phone' });
  expect(writes[1].headers['x-jarvis-owner-elevation']).toMatch(/^joe1_/); expect(writes[1].headers['x-jarvis-owner-key']).toBeUndefined();
  await expect(panel).toContainText('Some audio is missing'); expect(await panel.locator('img').count()).toBe(0);
  page.once('dialog', dialog => dialog.accept()); await panel.getByRole('button', { name: 'Delete recording', exact: true }).click();
  await expect(panel).toContainText('No interviews recorded yet.'); expect(deletions).toHaveLength(1);
  expect(deletions[0].body).toEqual({ revision: 3, locked: false }); expect(deletions[0].headers['x-jarvis-owner-elevation']).toMatch(/^joe1_/);
  expect(deletions[0].headers['x-jarvis-owner-key']).toBeUndefined();
});

test('unconfigured storage cannot prepare recordings and hiding the page clears its private recording view', async ({ page }) => {
  await connect(page);
  await page.route('**/api/recordings/status', r => r.fulfill({ json: { ready: false, consent } }));
  await page.route('**/api/recordings?locked=false', r => r.fulfill({ json: [{ id: '12345678-1111-4111-8111-111111111111', title: 'PRIVATE RECORDING TITLE', revision: 1, status: 'Prepared', durationSamples: 0, audioDeleted: false }] }));
  await page.getByRole('button', { name: 'Interview recordings', exact: true }).click(); const panel = page.locator('[data-panel="recordings"]');
  await expect(panel).toContainText('Encrypted recording storage is not configured');
  await expect(panel.getByRole('button', { name: 'Prepare recording', exact: true })).toHaveCount(0);
  await expect(panel).toContainText('PRIVATE RECORDING TITLE');
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(panel).not.toContainText('PRIVATE RECORDING TITLE'); await expect(panel.getByRole('button', { name: 'Delete recording', exact: true })).toHaveCount(0);
});
