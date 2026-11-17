const { test, expect } = require('@playwright/test');

test('shows the active voice and renews on the same socket and microphone', async ({ page, context }) => {
  await context.grantPermissions(['microphone']);
  let stream;
  let connections = 0;
  let authentication;
  let request;
  let confirmation;
  await page.routeWebSocket('**/voice-stream', socket => {
    stream = socket;
    connections++;
    socket.onMessage(raw => {
      const frame = JSON.parse(raw);
      if (frame.type === 'authenticate') {
        authentication = frame;
        socket.send(JSON.stringify({ type: 'ready', window: 1, voiceName: 'cedar', speed: 0.9,
          mode: 'Everyday', recommendedMode: 'Serious' }));
      }
      if (frame.type === 'voice_mode') {
        request = frame;
        socket.send(JSON.stringify({ type: 'voice_mode_offer', window: 1, offerId: 'offer-test', maximumGbpForTenMinutes: 0.091 }));
      }
      if (frame.type === 'confirm_voice_mode') {
        confirmation = frame;
        socket.send(JSON.stringify({ type: 'voice_mode_queued', window: 1, mode: 'Serious' }));
      }
    });
  });
  await page.goto('/');
  await page.evaluate(async () => {
    const { initLiveVoice } = await import('/live.js');
    window.microphoneStarts = 0;
    const openMicrophone = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
    navigator.mediaDevices.getUserMedia = (...args) => {
      window.microphoneStarts++;
      return openMicrophone(...args);
    };
    const container = document.createElement('div');
    container.dataset.panel = 'sessions';
    document.body.replaceChildren(container);
    const session = { id: 'session-fixture' };
    const liveVoice = initLiveVoice({
      api: async path => {
        if (path === '/voice/status') return { ready: true, detail: 'Ready.' };
        if (path === '/voice/tickets') return { ticket: 'ticket-fixture' };
        if (path === '/devices/browser-fixture/heartbeat') return {};
        throw Error(`Unexpected request to ${path}`);
      },
      selectedSession: () => session,
      ensureSession: async () => session,
      claimSession: async () => {},
      deviceId: 'browser-fixture',
      notice: () => {},
      beforeStart: async () => {},
    });
    await liveVoice.refresh();
  });
  await page.getByLabel(/I allow live audio/).check();
  await page.getByRole('button', { name: 'Start live voice' }).click();
  await expect(page.getByRole('status')).toContainText('voice cedar');
  expect(authentication).toEqual({ type: 'authenticate', ticket: 'ticket-fixture', continuous: true });
  await expect(page.getByRole('status')).toContainText('Everyday mode');
  await page.getByRole('button', { name: 'Request Serious voice' }).click();
  await expect(page.getByText('Serious voice reserves up to £0.10 per 10 minutes.')).toBeVisible();
  expect(request).toEqual({ type: 'voice_mode', mode: 'Serious', window: 1 });
  expect(confirmation).toBeUndefined();
  await page.getByRole('button', { name: 'Confirm Serious voice' }).click();
  await expect(page.getByText('Serious voice queued for the next renewal.')).toBeVisible();
  expect(confirmation).toEqual({ type: 'confirm_voice_mode', offerId: 'offer-test', window: 1 });
  await expect(page.getByRole('status')).toContainText('Everyday mode');
  stream.send(JSON.stringify({ type: 'window_renewing', window: 1 }));
  await expect(page.getByRole('status')).toHaveText('Renewing voice…');
  stream.send(JSON.stringify({ type: 'ready', window: 2, voiceName: 'marin', speed: 1, mode: 'Serious' }));
  await expect(page.getByRole('status')).toContainText('voice marin');
  await expect(page.getByRole('status')).toContainText('Serious mode');
  await expect(page.getByRole('button', { name: 'Confirm Serious voice' })).toBeHidden();
  expect(connections).toBe(1);
  expect(await page.evaluate(() => window.microphoneStarts)).toBe(1);
  await page.getByRole('button', { name: 'Stop live voice' }).click();
  await expect(page.getByRole('status')).toHaveText('Live microphone and playback stopped.');
});
