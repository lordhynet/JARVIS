const { test, expect } = require('@playwright/test');
const key = 'browser-test-only-key';
async function connect(page) {
  await page.goto('/');
  await page.getByLabel('API key', { exact: true }).fill(key);
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await expect(page.locator('#workspace')).toBeVisible();
}
test('desktop and mobile: create, checkpoint, pause, resume, and end a real session', async ({ page }) => {
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  await connect(page);
  await page.locator('#session-title').fill('Browser life scenario');
  await page.getByRole('button', { name: 'Start a conversation' }).click();
  await expect(page.locator('#selected-title')).toHaveText('Browser life scenario');
  await page.getByText('Save a checkpoint', { exact: true }).click();
  await page.locator('#checkpoint-summary').fill('Explained two pointers; next discuss complexity.');
  await page.locator('#checkpoint-task').fill('Two Sum II');
  await page.getByRole('button', { name: 'Save progress' }).click();
  await expect(page.locator('#checkpoints')).toContainText('Explained two pointers');
  await page.getByRole('button', { name: 'Pause', exact: true }).click(); await expect(page.locator('#session-status')).toHaveText('Paused');
  await page.getByRole('button', { name: 'Resume', exact: true }).click(); await expect(page.locator('#session-status')).toHaveText('Active');
  await page.locator('#message').fill('Tell me about binary search'); await page.getByRole('button', { name: 'Send message' }).click();
  await expect(page.locator('#notice')).toContainText('Configure OpenAI');
  await page.getByRole('button', { name: 'End session' }).click(); await expect(page.locator('#session-status')).toHaveText('Ended');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
  await page.screenshot({ path: `test-results/companion-${test.info().project.name}.png`, fullPage: true });
});
test('learn a French phrase and schedule the next review', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Learning', exact: true }).click();
  await page.locator('#learning-domain').selectOption('French');
  await page.locator('#learning-prompt').fill('How do you say thank you?'); await page.locator('#learning-answer').fill('Merci');
  await page.getByRole('button', { name: 'Add learning card' }).click();
  const card = page.locator('#learning-list .list-item').filter({ hasText: 'How do you say thank you?' }).last();
  await card.getByRole('button', { name: 'Reveal answer' }).click(); await expect(card).toContainText('Merci');
  await card.getByRole('button', { name: 'Good', exact: true }).click(); await expect(card).toHaveCount(0);
});
test('home action is reviewable and rejecting it has no side effect', async ({ page }) => {
  await connect(page); await page.locator('#session-title').fill('Home scenario'); await page.getByRole('button', { name: 'Start a conversation' }).click();
  await expect(page.locator('#selected-title')).toHaveText('Home scenario');
  await page.getByRole('button', { name: 'Home & tools' }).click(); await page.locator('#home-entity').fill('light.desk');
  await page.getByRole('button', { name: 'Prepare action' }).click();
  const tool = page.locator('#tool-list .list-item').filter({ hasText: 'AwaitingConfirmation' });
  await expect(tool).toContainText('turn_on'); await tool.getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(page.locator('#tool-list')).toContainText('Rejected by user.');
});
test('register browser and refuse voice before hardware verification', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: 'Devices', exact: true }).click();
  await page.locator('#device-name').fill('Browser test device'); await page.getByRole('button', { name: 'Register this device', exact: true }).click();
  await expect(page.locator('#device-list')).toContainText('Needs capability test');
  await page.getByRole('button', { name: 'Conversations', exact: true }).click(); await page.getByRole('button', { name: 'Start a conversation' }).click();
  await expect(page.locator('#session-status')).toHaveText('Active');
  await page.getByRole('button', { name: 'Use this device', exact: true }).click(); await expect(page.locator('#notice')).toContainText('Verify the device');
  await page.getByRole('button', { name: 'Record voice', exact: true }).click(); await expect(page.locator('#notice')).toContainText('Verify this device');
});
test('unauthenticated API is denied and key is never written to browser storage', async ({ page }) => {
  await page.goto('/'); const result = await page.request.get('/api/learning'); expect(result.status()).toBe(401);
  await connect(page); const storage = await page.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage })); expect(storage).not.toContain(key);
  await page.getByRole('button', { name: 'Connections', exact: true }).click(); await page.getByRole('button', { name: 'Disconnect this tab' }).click();
  await expect(page.locator('#login')).toBeVisible(); await expect(page.locator('#workspace')).toBeHidden();
});
test('simulated microphone: recording waits for stop and reports missing cloud setup', async ({ page, context }) => {
  await context.grantPermissions(['microphone']);
  await connect(page); await page.getByRole('button', { name: 'Devices', exact: true }).click();
  await page.getByRole('button', { name: 'Register this device', exact: true }).click();
  await page.getByRole('button', { name: 'Test microphone & speaker', exact: true }).click();
  // Chromium supplies synthetic microphone input. This is not a physical acoustic test.
  await page.getByRole('button', { name: 'I heard the speaker test — verify this device' }).click();
  await page.getByRole('button', { name: 'Conversations', exact: true }).click(); await page.getByRole('button', { name: 'Start a conversation' }).click();
  await expect(page.locator('#session-status')).toHaveText('Active'); await page.getByRole('button', { name: 'Use this device', exact: true }).click();
  await expect(page.locator('#owner')).toContainText('this browser');
  let uploads = 0; page.on('request', r => { if (r.url().endsWith('/api/voice/transcribe')) uploads++; });
  await page.getByRole('button', { name: 'Record voice', exact: true }).click(); await expect(page.locator('#notice')).toContainText('Recording.');
  expect(uploads).toBe(0); await page.waitForTimeout(1200);
  await page.getByRole('button', { name: 'Stop & transcribe', exact: true }).click();
  await expect(page.locator('#notice')).toContainText('Configure OpenAI'); expect(uploads).toBe(1);
});
test('two browser devices transfer a live recording session without overlapping ownership', async ({ page, context, browser }) => {
  test.setTimeout(60000);
  await context.grantPermissions(['microphone']);
  const targetContext = await browser.newContext({ permissions: ['microphone'], baseURL: 'http://127.0.0.1:5197' });
  const target = await targetContext.newPage();
  try {
    async function register(p, name) {
      await connect(p); await p.getByRole('button', { name: 'Devices', exact: true }).click();
      await p.locator('#device-name').fill(name); await p.getByRole('button', { name: 'Register this device', exact: true }).click();
      await p.getByRole('button', { name: 'Test microphone & speaker', exact: true }).click();
      await p.getByRole('button', { name: 'I heard the speaker test — verify this device' }).click();
    }
    await register(page, 'Source test browser'); await register(target, 'Target test browser');
    const targetId = await target.evaluate(() => localStorage.getItem('jarvis-device'));
    await page.getByRole('button', { name: 'Conversations', exact: true }).click();
    const title = `Handoff ${Date.now()}`; await page.locator('#session-title').fill(title); await page.getByRole('button', { name: 'Start a conversation' }).click();
    await expect(page.locator('#selected-title')).toHaveText(title); await page.getByRole('button', { name: 'Use this device', exact: true }).click();
    await expect(page.locator('#owner')).toContainText('this browser'); await page.getByRole('button', { name: 'Record voice', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop & transcribe', exact: true })).toBeVisible();
    await target.getByRole('button', { name: 'Conversations', exact: true }).click();
    await target.locator('#session-list button').filter({ hasText: title }).click();
    await page.locator('#target-device').selectOption(targetId); await page.getByRole('button', { name: 'Move conversation' }).click();
    await target.getByRole('button', { name: 'Ready on this device', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Record voice', exact: true })).toBeVisible({ timeout: 15000 });
    await target.getByRole('button', { name: 'Take over here', exact: true }).click();
    await expect(target.locator('#owner')).toContainText('this browser');
    await expect(page.locator('#owner')).toContainText(targetId, { timeout: 15000 });
    await expect(target.locator('#handoff-state')).toContainText('Completed');
  } finally { await targetContext.close(); }
});
