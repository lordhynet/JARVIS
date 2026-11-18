const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');

test('held agent setup scopes owner authorization and reviews exact proposed parameters', async ({ page }) => {
  const runId = randomUUID(), actionId = randomUUID(), actionHash = 'a'.repeat(64), grantId = randomUUID();
  const args = '{"entityId":"light.kitchen","action":"turn_on","note":"<img src=x onerror=alert(1)>"}';
  let held = true, approved = false, hasGrant = false;
  await page.route('**/api/agents/runs', route => route.fulfill({ json: [{ run: { id: runId, workflow: 'personal-plan', status: held ? 'AwaitingSetup' : 'Running' }, tasks: [{ role: 'PersonalPlanner', status: held ? 'Queued' : 'AwaitingApproval' }] }] }));
  await page.route('**/api/agents/actions', route => route.fulfill({ json: held ? [] : [{ proposal: { id: actionId, runId, capabilityId: 'homeassistant.turn', argumentsJson: args, expiresAt: new Date(Date.now() + 120000).toISOString() }, status: approved ? 'AwaitingReceipt' : 'AwaitingApproval', argumentsHash: actionHash, risk: 'Reversible' }] }));
  await page.route('**/api/agents/tool-grants', route => route.fulfill({ json: hasGrant ? [{ id: grantId, runId, capabilityId: 'homeassistant.turn', maximumCalls: 4, maximumUsd: 0.5, grantedBy: 'owner', preApproved: false }] : [] }));
  await page.route(`**/api/agents/runs/${runId}/tool-grants`, async route => {
    expect(route.request().headers()['x-jarvis-owner-key']).toBeTruthy(); expect(route.request().postDataJSON().capabilityId).toBe('homeassistant.turn');
    hasGrant = true; await route.fulfill({ json: {} });
  });
  await page.route(`**/api/agents/runs/${runId}/release`, async route => { expect(route.request().headers()['x-jarvis-owner-key']).toBeTruthy(); held = false; await route.fulfill({ json: {} }); });
  await page.route(`**/api/agents/actions/${actionId}/approve`, async route => {
    expect(route.request().headers()['x-jarvis-owner-key']).toBeTruthy(); expect(route.request().postDataJSON()).toEqual({ argumentsHash: actionHash });
    approved = true; await route.fulfill({ status: 204 });
  });
  const reads = []; page.on('request', r => { if (r.url().endsWith('/api/agents/actions')) reads.push(r.headers()['x-jarvis-owner-key']); });
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Proactive agents', exact: true }).click();
  const panel = page.locator('[data-panel=agents]');
  await panel.getByLabel('Agent action capability').selectOption('homeassistant.turn');
  await panel.getByRole('button', { name: 'Grant capability for this run', exact: true }).click();
  await expect(panel).toContainText('Exact approval required');
  await expect(panel).toContainText('0.5 USD per call'); await expect(panel).toContainText('up to 4 calls');
  await expect(panel.getByLabel('Agent action capability').locator('option[value="windows.notification.show"]')).toHaveCount(0);
  await expect(panel.getByLabel('Agent action capability').locator('option[value="windows.files.find"]')).toHaveCount(0);
  await panel.getByRole('button', { name: 'Start configured run', exact: true }).click();
  await expect(panel).toContainText(args); await expect(panel.locator('img')).toHaveCount(0); await expect(panel).toContainText(actionHash);
  page.once('dialog', d => d.accept()); await panel.getByRole('button', { name: 'Approve exact agent action', exact: true }).click();
  await expect(panel).toContainText('Waiting for a verified device receipt. Completion has not been confirmed.');
  expect(reads.length).toBeGreaterThan(0); expect(reads.every(value => !value)).toBeTruthy();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
});
