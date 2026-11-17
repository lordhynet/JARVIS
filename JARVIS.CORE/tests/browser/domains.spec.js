const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');
test('domain preview binds manual approval to the exact version and scopes the owner header', async ({ page }) => {
  const headers = { 'X-Jarvis-Key': 'browser-test-only-key' };
  const id = randomUUID().slice(0, 8), key = 'education-' + id;
  const draft = await page.request.post('/api/domains/drafts', { headers, data: { requestId: randomUUID(), manifest: {
    schemaVersion: 1, key, displayName: 'Education ' + id, description: 'Explain a topic. <img src=x onerror=alert(1)>', aliases: [], roles: ['TechnicalTrainer'],
    capabilities: [{ id: 'core_template_practice', versionConstraint: '1', isRequired: true }], practicePrompts: ['Give a worked example.'], executionMode: 'Template'
  } } }); expect(draft.ok()).toBeTruthy(); const preview = await draft.json();
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill(headers['X-Jarvis-Key']);
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Proactive agents', exact: true }).click();
  const row = page.locator('[data-panel=agents] .list-item').filter({ hasText: 'Education ' + id });
  await expect(row).toContainText('no shared personal coaching context'); await expect(row.locator('img')).toHaveCount(0);
  await expect(row.getByLabel('Approval risk class')).toHaveValue('General'); await row.getByLabel('Approval risk class').selectOption('Finance');
  await row.getByText('Exact manifest to approve', { exact: true }).click(); await expect(row).toContainText(preview.version.manifestHash);
  page.once('dialog', d => d.accept()); const activated = page.waitForRequest(r => r.url().endsWith('/domains/' + key + '/activate'));
  await row.getByRole('button', { name: 'Approve and activate this version', exact: true }).click(); const request = await activated;
  expect(request.headers()['x-jarvis-owner-key']).toBeTruthy(); expect(request.postDataJSON().versionId).toBe(preview.version.id); expect(request.postDataJSON().manifestHash).toBe(preview.version.manifestHash); expect(request.postDataJSON().riskClass).toBe('Finance');
  await expect(row).toContainText('Active');
  await expect(page.locator('[data-panel=agents]').getByLabel('Workflow', { exact: true }).locator(`option[value="domain-${key}-practice"]`)).toHaveCount(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});
