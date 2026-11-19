const { test, expect } = require('@playwright/test');
const id = '12345678-1111-4111-8111-111111111111';
const quoteId = '22345678-1111-4111-8111-111111111111';
const pieces = ['32345678-1111-4111-8111-111111111111', '42345678-1111-4111-8111-111111111111'];
const cloud = 'I confirm this quote and consent to send its audio to my configured transcription provider for my own practice.';
const transcript = (partial, count) => ({ id: '52345678-1111-4111-8111-111111111111', revision: count, partial, timingSource: 'Capture intervals; not word timing',
  segments: pieces.slice(0, count).map((piece, i) => ({ recordingPieceId: piece, speaker: 'unknown', startSeconds: i * 60, endSeconds: (i + 1) * 60, text: i ? 'My second private answer.' : '<img src=x onerror=alert(1)> private answer' })) });
function quoted() { return { recordingId: id, quoteId, revision: 1, recordingRevision: 2, status: 'Quoted', model: 'synthetic-only', maximumGbp: .04, maximumUsd: .04,
  quoteExpiresAt: new Date(Date.now() + 600000).toISOString(), pieces: pieces.map(piece => ({ id: piece, status: 'Pending' })), transcript: null }; }
async function setup(page, handle) {
  await page.route('**/api/recordings/status', r => r.fulfill({ json: { ready: false, consent: 'Owner authorization' } }));
  await page.route('**/api/recordings?locked=false', r => r.fulfill({ json: [{ id, title: 'My private interview', status: 'Complete', revision: 1, durationSamples: 1920000, audioDeleted: false }] }));
  await page.route(`**/api/recordings/${id}/transcription**`, handle);
  await page.goto('/'); await page.getByLabel('API key', { exact: true }).fill('browser-test-only-key'); await page.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByRole('button', { name: 'Interview recordings', exact: true }).click();
  await page.getByRole('button', { name: 'Transcript and speakers', exact: true }).click();
}
test('a confirmed quote transcribes bounded pieces, renders literal text and saves owner speaker labels', async ({ page }) => {
  let state = null; const requests = [], labels = [];
  await setup(page, r => {
    const request = r.request(); const path = new URL(request.url()).pathname;
    if (request.method() === 'GET') return state ? r.fulfill({ json: state }) : r.fulfill({ status: 204, body: '' });
    if (path.endsWith('/quote')) { state = quoted(); return r.fulfill({ json: state }); }
    if (path.endsWith('/advance')) {
      requests.push(request.postDataJSON()); const count = requests.length;
      state = { ...state, revision: state.revision + 1, status: count === 1 ? 'Ready' : 'Complete', transcript: transcript(count === 1, count),
        pieces: state.pieces.map((piece, i) => ({ ...piece, status: i < count ? 'Succeeded' : 'Pending' })) };
      return r.fulfill({ json: state });
    }
    labels.push(request.postDataJSON()); state.transcript.segments[0].speaker = 'user'; state.transcript.revision++; return r.fulfill({ json: state.transcript });
  });
  await expect(page.getByText('No transcription requested. Review a quote before sending audio.')).toBeVisible(); expect(requests).toHaveLength(0);
  await page.getByRole('button', { name: 'Get transcription quote', exact: true }).click(); await expect(page.getByText(/Maximum for this quote £0.04/)).toBeVisible();
  await page.getByRole('button', { name: 'Confirm and transcribe', exact: true }).click(); await expect(page.locator('#notice')).toContainText('Confirm the quote'); expect(requests).toHaveLength(0);
  await page.getByLabel(cloud, { exact: true }).check(); await page.getByRole('button', { name: 'Confirm and transcribe', exact: true }).click();
  await expect(page.getByText('My second private answer.', { exact: true })).toBeVisible(); expect(requests).toHaveLength(2);
  expect(requests[0]).toMatchObject({ quoteId, confirmCloudProcessing: true, confirmQuote: true, locked: false });
  expect(requests[1].requestId).not.toBe(requests[0].requestId); expect(requests[1].confirmQuote).toBe(false);
  expect(await page.locator('[data-panel="recordings"] img').count()).toBe(0);
  await page.getByRole('button', { name: 'Me', exact: true }).first().click(); await expect(page.getByText('0.0–60.0s · user', { exact: true })).toBeVisible();
  expect(labels[0]).toEqual({ transcriptRevision: 2, pieceId: pieces[0], speaker: 'user', locked: false });
});
test('an unknown response stops the paid sequence and requires a deliberate refresh and billing reconciliation', async ({ page }) => {
  let state = null, advances = 0;
  await setup(page, r => {
    const request = r.request();
    if (request.method() === 'GET') return state ? r.fulfill({ json: state }) : r.fulfill({ status: 204, body: '' });
    if (request.url().endsWith('/quote')) { state = quoted(); return r.fulfill({ json: state }); }
    advances++; state = { ...state, revision: 3, status: 'Uncertain', errorCode: 'provider_outcome_requires_reconciliation', pieces: state.pieces.map((p, i) => i ? p : { ...p, status: 'Uncertain', reservationId: 'synthetic-reservation' }) };
    return r.abort();
  });
  await page.getByRole('button', { name: 'Get transcription quote', exact: true }).click(); await page.getByLabel(cloud, { exact: true }).check();
  await page.getByRole('button', { name: 'Confirm and transcribe', exact: true }).click(); await expect(page.locator('#notice')).toContainText('no paid request was retried'); expect(advances).toBe(1);
  await page.getByRole('button', { name: 'Refresh transcription progress', exact: true }).click(); await expect(page.getByText(/Verify provider billing/)).toBeVisible(); expect(advances).toBe(1);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.locator('[data-panel="recordings"]')).not.toContainText('synthetic-reservation');
});
