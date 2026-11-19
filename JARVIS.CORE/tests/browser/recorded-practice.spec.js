const { test, expect } = require('@playwright/test');
const recording = '12345678-2222-4222-8222-111111111111', question = '22345678-2222-4222-8222-111111111111', piece = '32345678-2222-4222-8222-111111111111';
const consent = 'I consent to use these real questions and my answers with my configured normal voice provider for this private practice.';
const delivery = { words: 7, wordsPerMinute: 7, fillers: 1, seconds: 60, openingScore: 1, pauseRatio: null, timingSource: 'Whole capture interval; pauses unavailable' };
function source() {
  return { recordingId: recording, documentRevision: 3, partial: true, timingSource: 'Capture intervals, not word timings', thankYouDraft: 'Thank you for your time. [your name]',
    questions: [{ id: question, question: '<img src=x onerror=alert(1)> Why retries?', startSeconds: 0, endSeconds: 60, originalAnswer: 'I chose retries because the network failed.',
      alignment: 'Capture interval only — not precise question/answer timing.', delivery, coverage: [{ dimension: 'Evidence', score: 0, maximum: 4, basis: 'Text markers only' }],
      gaps: ['Verify evidence from your own source.'], audioPieces: [piece], stories: [], cardApproved: false }],
    practices: [{ id: 'fixture-practice', mode: 'practice', status: 'Question', sessionId: 'fixture-session', sourceCurrent: true,
      answers: [{ capturedAnswer: 'My captured replacement answer.', comparison: { before: delivery, after: { ...delivery, words: 12, wordsPerMinute: 72, seconds: 10, pauseRatio: .2 },
        beforeCoverage: [{ dimension: 'Evidence', score: 0, maximum: 4 }], afterCoverage: [{ dimension: 'Evidence', score: 2, maximum: 4 }] } }] },
      { id: 'fixture-recap', mode: 'recap', status: 'Completed', sessionId: 'fixture-recap-session', sourceCurrent: true, capturedRecap: 'I felt calm. Next step: follow up tomorrow.' }] };
}
async function setup(page, handler) {
  await page.route('**/api/recordings/status', r => r.fulfill({ json: { ready: false } }));
  await page.route('**/api/recordings?locked=false', r => r.fulfill({ json: [{ id: recording, title: 'Private interview', revision: 1, status: 'Complete', durationSamples: 1120000, audioDeleted: false }] }));
  await page.route(`**/api/recordings/${recording}/practice**`, handler);
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key');
  await page.getByLabel('Owner key (optional)', { exact: true }).fill('browser-test-only-owner-management-123456789');
  await page.getByRole('button', { name: 'Connect', exact: true }).click(); await page.getByRole('button', { name: 'Interview recordings', exact: true }).click();
}
test('private timeline compares real answers, uses authenticated inline audio, approves cards explicitly and clears on hide', async ({ page }) => {
  let state = source(); const approvals = [], audioRequests = [];
  await setup(page, r => {
    const request = r.request(), path = new URL(request.url()).pathname;
    if (path.includes('/audio/')) { audioRequests.push(request.headers()); return r.fulfill({ contentType: 'audio/wav', body: Buffer.alloc(44) }); }
    if (path.endsWith('/cards')) { approvals.push(request.postDataJSON()); state.questions[0].cardApproved = true; return r.fulfill({ json: {} }); }
    return r.fulfill({ json: state });
  });
  await page.getByRole('button', { name: 'Review and practise', exact: true }).click();
  const panel = page.locator('[data-panel="recordings"]'); await expect(panel.getByText('My captured replacement answer.', { exact: true })).toBeVisible();
  await expect(panel.getByText('Original answer (interval estimate)', { exact: true })).toBeVisible(); await expect(panel.getByText('New captured answer', { exact: true })).toBeVisible();
  await expect(panel.getByText('I felt calm. Next step: follow up tomorrow.', { exact: true })).toBeVisible();
  await expect(panel.locator('img')).toHaveCount(0); expect(approvals).toHaveLength(0); expect(audioRequests).toHaveLength(0);
  await page.evaluate(() => { window.removedAudioUrls = []; const revoke = URL.revokeObjectURL.bind(URL); URL.revokeObjectURL = url => { window.removedAudioUrls.push(url); revoke(url); }; });
  await panel.getByRole('button', { name: 'Load my answer audio', exact: true }).click(); await expect(panel.locator('audio')).toHaveAttribute('src', /^blob:/);
  expect(audioRequests[0]['x-jarvis-key']).toBeTruthy(); expect(audioRequests[0]['x-jarvis-owner-key']).toBeTruthy(); await expect(panel.locator('a[download]')).toHaveCount(0);
  await panel.getByRole('button', { name: 'Approve practice card', exact: true }).click(); await expect(panel.getByText('Practice card approved.', { exact: true })).toBeVisible();
  expect(approvals).toEqual([{ documentRevision: 3, questionId: question, confirm: true, locked: false }]);
  await expect(panel.getByRole('heading', { name: 'Thank-you note — draft only', exact: true })).toBeVisible(); await expect(panel.getByRole('button', { name: /send/i })).toHaveCount(0);
  expect(await page.evaluate(() => window.removedAudioUrls.length)).toBe(1);
  await panel.getByRole('button', { name: 'Load my answer audio', exact: true }).click();
  // Wait for the second authenticated audio URL; hiding earlier tests the separate late-result path, not cleanup.
  await expect(panel.locator('audio')).toHaveAttribute('src', /^blob:/);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(panel).not.toContainText('My captured replacement answer.'); await expect(panel.locator('audio')).toHaveCount(0); expect(await page.evaluate(() => window.removedAudioUrls.length)).toBe(2);
});
test('practice and recap require explicit cloud voice consent and never auto retry a failed start', async ({ page }) => {
  const starts = [];
  await setup(page, r => {
    if (r.request().method() === 'GET') return r.fulfill({ json: source() });
    starts.push(r.request().postDataJSON()); return r.abort();
  });
  await page.getByRole('button', { name: 'Review and practise', exact: true }).click();
  await page.getByRole('button', { name: 'Practise this interview', exact: true }).click(); await expect(page.locator('#notice')).toContainText('Confirm normal cloud voice consent first'); expect(starts).toHaveLength(0);
  await page.getByLabel(consent, { exact: true }).check(); await page.getByRole('button', { name: 'Practise this interview', exact: true }).click(); await expect(page.locator('#notice')).toContainText('fetch'); expect(starts).toHaveLength(1);
  await page.getByRole('button', { name: 'Practise this interview', exact: true }).click(); expect(starts).toHaveLength(2); expect(starts[0].requestId).toBe(starts[1].requestId);
  expect(starts[0]).toMatchObject({ documentRevision: 3, mode: 'practice', confirmCloudPractice: true, locked: false });
  await page.getByRole('button', { name: 'Interview just finished', exact: true }).click(); await page.getByRole('button', { name: 'Start voice recap', exact: true }).click(); expect(starts).toHaveLength(2);
  await page.getByLabel('I consent to normal cloud voice processing for my private interview recap.', { exact: true }).check(); await page.getByRole('button', { name: 'Start voice recap', exact: true }).click();
  expect(starts[2]).toMatchObject({ documentRevision: null, mode: 'recap', confirmCloudPractice: true, locked: false });
});
test('approved private learning references reopen their source without copying question text into the card', async ({ page }) => {
  await page.route('**/api/learning?due=true', r => r.fulfill({ json: [{ id: 'private-card', sourceRecordingId: recording, domain: 'InterviewStory', prompt: 'Private interview question — open its interview practice page', answer: 'Use your own evidence.' }] }));
  await setup(page, r => r.fulfill({ json: source() }));
  await page.getByRole('button', { name: 'Learning', exact: true }).click(); await expect(page.locator('[data-panel="learning"]')).not.toContainText('Why retries?');
  await page.getByRole('button', { name: 'Open private interview practice', exact: true }).click();
  await expect(page.locator('[data-panel="recordings"]')).toBeVisible(); await expect(page.getByText('My captured replacement answer.', { exact: true })).toBeVisible();
});
