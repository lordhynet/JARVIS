const { test, expect } = require('@playwright/test');

test('shows the active voice and renews on the same socket and microphone', async ({ page, context }) => {
  await context.grantPermissions(['microphone']);
  let stream;
  let connections = 0;
  let authentication;
  await page.routeWebSocket('**/voice-stream', socket => {
    stream = socket;
    connections++;
    socket.onMessage(raw => {
      const frame = JSON.parse(raw);
      if (frame.type === 'authenticate') {
        authentication = frame;
        socket.send(JSON.stringify({ type: 'ready', window: 1, voiceName: 'cedar', speed: 0.9 }));
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
  stream.send(JSON.stringify({ type: 'window_renewing', window: 1 }));
  await expect(page.getByRole('status')).toHaveText('Renewing voice…');
  stream.send(JSON.stringify({ type: 'ready', window: 2, voiceName: 'marin', speed: 1 }));
  await expect(page.getByRole('status')).toContainText('voice marin');
  expect(connections).toBe(1);
  expect(await page.evaluate(() => window.microphoneStarts)).toBe(1);
  await page.getByRole('button', { name: 'Stop live voice' }).click();
  await expect(page.getByRole('status')).toHaveText('Live microphone and playback stopped.');
});
