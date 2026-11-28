const { test, expect } = require('@playwright/test');
const { randomUUID } = require('node:crypto');

// AI-generated practice in the guided view: labelled, never crashes while waiting for a question, and answered by voice only.
// Drill reads are stubbed with synthetic generated drills; nothing is generated or sent to a provider.
function generatedView(sessionId, { awaiting }) {
  const drillId = randomUUID(); const questionId = randomUUID();
  const step = { index: 0, questionId, questionVersion: 1, title: 'Clarifying requirements', prompt: 'What do you clarify first?',
    reference: 'Scale, latency and durability.', sourceUrl: '', generatedQuestionId: questionId };
  const drill = { id: drillId, sessionId, mode: 'RapidDrill', revision: 2, status: 'Active', createdAt: new Date().toISOString(),
    steps: awaiting ? [step] : [step], attemptIds: awaiting ? [randomUUID()] : [], requestedTopic: 'SystemDesign',
    generated: { topic: 'SystemDesign', pending: awaiting ? { drillRevision: 2, stepIndex: 1, outlineKey: 'data-ownership', outlineVersion: 1 } : null, questionsPrepared: 1 } };
  const answers = awaiting ? [{ id: drill.attemptIds[0], skipped: false, stepIndex: 0, currentAnswer: 'Latency targets first.', canonicalAssessment: null }] : [];
  return { drill, answers };
}

async function openWith(page, view) {
  await page.route('**/api/training/sessions/*/drills', route => route.fulfill({ json: [view] }));
  await page.route(`**/api/training/drills/${view.drill.id}`, route => route.fulfill({ json: view }));
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Technical practice', exact: true }).click();
  const panel = page.locator('[data-panel=training]');
  // Starting an authored drill creates the conversation the guided history is read from; its history read is stubbed.
  await panel.getByRole('button', { name: 'Start guided drill', exact: true }).click();
  await panel.getByRole('button', { name: 'Load saved guided drills', exact: true }).click();
  await panel.getByRole('button', { name: new RegExp('^AI-generated · RapidDrill · Active') }).click();
  return panel;
}

test('a generated drill waiting for its next question is labelled and points back to voice', async ({ page }) => {
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  const panel = await openWith(page, generatedView(randomUUID(), { awaiting: true }));
  await expect(panel).toContainText('AI-generated practice. Questions and reference answers are AI-written and not verified.');
  await expect(panel).toContainText('Waiting for the next AI-generated question. Continue this practice by voice.');
  await expect(panel.getByLabel('Guided answer', { exact: true })).toHaveCount(0);
  await panel.getByText('Saved stage 1 · unrated', { exact: true }).click();
  await expect(panel).toContainText('AI-written reference (not verified): Scale, latency and durability.');
  expect(errors).toEqual([]);
});

test('a generated question is shown for voice answering without a text form', async ({ page }) => {
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  const panel = await openWith(page, generatedView(randomUUID(), { awaiting: false }));
  await expect(panel).toContainText('Stage 1: Clarifying requirements');
  await expect(panel).toContainText('Answer this AI-generated question by voice.');
  await expect(panel.getByLabel('Guided answer', { exact: true })).toHaveCount(0);
  await expect(panel.getByRole('button', { name: 'Save guided stage', exact: true })).toHaveCount(0);
  expect(errors).toEqual([]);
});
