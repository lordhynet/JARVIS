const { test, expect } = require('@playwright/test');
test('locked-room speech stays visible with an audit-only label', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await expect(page.locator('#workspace')).toBeVisible();
  await page.route('**/api/sessions/*', async route => {
    const response = await route.fetch();
    if (route.request().method() !== 'GET' || !response.ok()) return route.fulfill({ response });
    const session = await response.json();
    if (!session.id || !session.checkpoints) return route.fulfill({ response });
    session.messages = [{ requestId: '11111111-1111-1111-1111-111111111111', role: 'user', text: '<script>unsafe locked speech</script>', at: new Date().toISOString(), isVoice: true, restricted: true },
      { requestId: '22222222-2222-2222-2222-222222222222', role: 'assistant', text: 'Trusted reply', at: new Date().toISOString(), isVoice: true, restricted: false }];
    await route.fulfill({ response, json: session });
  });
  await page.locator('#session-title').fill('Locked audit test');
  await page.getByRole('button', { name: 'Start a conversation' }).click();
  await expect(page.locator('#selected-title')).toHaveText('Locked audit test');
  // Opening the saved conversation reads the canonical audit transcript.
  await page.locator('#session-list button').filter({ hasText: 'Locked audit test' }).last().click();
  await expect(page.locator('#messages')).toContainText('Locked device — audit only');
  await expect(page.locator('#messages')).toContainText('<script>unsafe locked speech</script>');
  await expect(page.locator('#messages script')).toHaveCount(0);
  await expect(page.locator('#messages .assistant small')).toHaveText('JARVIS');
});
