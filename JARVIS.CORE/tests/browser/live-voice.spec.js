const { test, expect } = require('@playwright/test');

test('shows the active voice when Core opens a budgeted voice window', async ({ page, context }) => {
  await context.grantPermissions(['microphone']);
  await page.routeWebSocket('**/voice-stream', socket => {
    socket.onMessage(raw => {
      if (JSON.parse(raw).type === 'authenticate') {
        socket.send(JSON.stringify({ type: 'ready', voiceName: 'cedar', speed: 0.9 }));
      }
    });
  });
  await page.goto('/');
  await page.evaluate(async () => {
    const { initLiveVoice } = await import('/live.js');
    const container = document.createElement('div');
    container.dataset.panel = 'sessions';
    document.body.replaceChildren(container);
    const session = { id: 'session-fixture' };
    const liveVoice = initLiveVoice({
      api: async path => {
        if (path === '/voice/status') return { ready: true, detail: 'Ready.' };
        if (path === '/voice/tickets') return { ticket: 'ticket-fixture' };
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
  await page.getByRole('button', { name: 'Stop live voice' }).click();
  await expect(page.getByRole('status')).toHaveText('Live microphone and playback stopped.');
});
