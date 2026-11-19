const { test, expect } = require('@playwright/test');

test('shows the active voice and renews on the same socket and microphone', async ({ page, context }) => {
  await context.grantPermissions(['microphone']);
  let stream;
  let connections = 0;
  let authentication;
  let request;
  let confirmation;
  let audioCapture;
  let fallbackPlayed;
  let fallbackAudioCount = 0;
  await page.routeWebSocket('**/voice-stream', socket => {
    stream = socket;
    connections++;
    socket.onMessage(raw => {
      const frame = JSON.parse(raw);
      if (frame.type === 'authenticate') {
        authentication = frame;
        socket.send(JSON.stringify({ type: 'voice_mode_offer', starting: true, window: 1,
          offerId: 'starting-offer', maximumGbpForTenMinutes: 0.091 }));
      }
      if (frame.type === 'voice_mode' && frame.mode === 'Serious') {
        request = frame;
        socket.send(JSON.stringify({ type: 'voice_mode_offer', window: 1, offerId: 'offer-test', maximumGbpForTenMinutes: 0.091 }));
      }
      if (frame.type === 'confirm_voice_mode') {
        confirmation = frame;
        socket.send(JSON.stringify({ type: 'voice_mode_queued', window: 1, mode: 'Serious' }));
      }
      if (frame.type === 'voice_mode' && frame.offerId === 'starting-offer') {
        socket.send(JSON.stringify({ type: 'ready', window: 1, voiceName: 'cedar', speed: 0.9,
          mode: 'Everyday', recommendedMode: 'Serious' }));
      }
      if (frame.type === 'voice_fallback_played') fallbackPlayed = frame;
      if (frame.type === 'voice_fallback_audio') {
        fallbackAudioCount++;
        audioCapture = frame;
      }
      if (frame.type === 'heartbeat') socket.send(JSON.stringify({ type: 'lease', sequence: frame.sequence, leaseSeconds: 15 }));
    });
  });
  await page.goto('/');
  await page.evaluate(async () => {
    const { initLiveVoice } = await import('/live.js');
    window.microphoneStarts = 0;
    Object.defineProperty(window, 'speechSynthesis', { value: {
      getVoices: () => [{ localService: true, lang: 'en-GB' }],
      speak: utterance => { window.spokenFallback = utterance; },
      cancel: () => {},
    } });
    Object.defineProperty(window, 'SpeechSynthesisUtterance', { value: function (text) { this.text = text; } });
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
  await expect(page.getByRole('status')).toHaveText('Review the Serious voice estimate to continue.');
  expect(authentication).toEqual({ type: 'authenticate', ticket: 'ticket-fixture', continuous: true, voiceModeNegotiation: true });
  expect(confirmation).toBeUndefined();
  await page.getByRole('button', { name: 'Use Everyday voice' }).click();
  await expect(page.getByRole('status')).toContainText('voice cedar');
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
  stream.send(JSON.stringify({ type: 'window_renewing', window: 2 }));
  stream.send(JSON.stringify({ type: 'voice_fallback_offer', captureId: 'fallback-fixture',
    detail: 'Serious voice is unavailable.', prompt: 'Say yes to continue in Everyday voice.' }));
  await expect(page.getByRole('status')).toContainText('Say yes to continue');
  await expect.poll(() => page.evaluate(() => window.spokenFallback?.text)).toBe('Say yes to continue in Everyday voice.');
  expect(fallbackPlayed).toBeUndefined();
  expect(fallbackAudioCount).toBe(0);
  await page.evaluate(() => window.spokenFallback.onend());
  await expect.poll(() => fallbackPlayed).toEqual({ type: 'voice_fallback_played', captureId: 'fallback-fixture' });
  expect(fallbackAudioCount).toBe(0);
  stream.send(JSON.stringify({ type: 'voice_fallback_capture', captureId: 'fallback-fixture' }));
  await expect.poll(() => audioCapture?.captureId).toBe('fallback-fixture');
  stream.send(JSON.stringify({ type: 'voice_fallback_verifying', captureId: 'fallback-fixture' }));
  await expect(page.getByRole('status')).toHaveText('Verifying your answer…');
  stream.send(JSON.stringify({ type: 'ready', window: 3, voiceName: 'cedar', mode: 'Everyday' }));
  await expect(page.getByRole('status')).toContainText('Everyday mode');
  expect(connections).toBe(1);
  expect(await page.evaluate(() => window.microphoneStarts)).toBe(1);
  await page.getByRole('button', { name: 'Stop live voice' }).click();
  await expect(page.getByRole('status')).toHaveText('Live microphone and playback stopped.');
});
