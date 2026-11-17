export function initLiveVoice({ api, selectedSession, ensureSession, claimSession, deviceId, notice, beforeStart }) {
  const panel = document.createElement('section'); panel.className = 'card live-voice';
  const title = document.createElement('h2'); title.textContent = 'Live voice coaching'; panel.append(title);
  const description = document.createElement('p'); description.textContent = 'Speak naturally, interrupt replies, and keep the same Core session. Audio is streamed to OpenAI only after you start.'; panel.append(description);
  const label = document.createElement('label'); const consent = document.createElement('input'); consent.type = 'checkbox'; consent.id = 'live-cloud-consent'; label.htmlFor = consent.id;
  label.append(consent, document.createTextNode(' I allow live audio and selected session context to be processed by OpenAI for this voice session.')); panel.append(label);
  const start = document.createElement('button'); start.textContent = 'Start live voice'; start.disabled = true; start.className = 'primary';
  const stop = document.createElement('button'); stop.textContent = 'Stop live voice'; stop.disabled = true;
  const interrupt = document.createElement('button'); interrupt.textContent = 'Interrupt reply'; interrupt.disabled = true;
  const status = document.createElement('p'); status.setAttribute('role', 'status'); status.textContent = 'Connect to Core to check live voice readiness.';
  const modeControls = document.createElement('div'); modeControls.hidden = true;
  const everyday = document.createElement('button'); everyday.textContent = 'Use Everyday voice';
  const serious = document.createElement('button'); serious.textContent = 'Request Serious voice';
  const confirmSerious = document.createElement('button'); confirmSerious.textContent = 'Confirm Serious voice'; confirmSerious.hidden = true;
  const modeNotice = document.createElement('p'); modeNotice.setAttribute('aria-live', 'polite');
  const restartEveryday = document.createElement('button'); restartEveryday.textContent = 'Start in Everyday voice'; restartEveryday.hidden = true;
  modeControls.append(everyday, serious, confirmSerious, modeNotice);
  const transcript = document.createElement('div'); transcript.className = 'list';
  panel.append(start, stop, interrupt, restartEveryday, status, modeControls, transcript); document.querySelector('[data-panel=sessions]').prepend(panel);
  let capability = null, active = false, ready = false, socket, media, context, capture, source, heartbeat, watchdog, leaseDeadline = 0, sequence = 0;
  let nextAudio = 0, currentItem = null, itemStart = 0, itemSamples = 0; const sounds = new Set(); const pings = new Map(); let generation = 0, renewWanted = false;
  let windowNumber = 0, offerId = null, startEveryday = false;
  function clearOffer() {
    offerId = null;
    confirmSerious.hidden = true;
  }
  function sendMode(frame) {
    if (active && ready && socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ ...frame, window: windowNumber }));
  }
  function clearPlayback(send = false) {
    if (send && currentItem && ready && socket?.readyState === WebSocket.OPEN) {
      const playedMs = Math.max(0, Math.min(itemSamples / 24, (context.currentTime - itemStart) * 1000));
      socket.send(JSON.stringify({ type: 'interrupt', window: windowNumber, itemId: currentItem, playedMs: Math.floor(playedMs) }));
    }
    for (const sound of sounds) { try { sound.stop(); } catch {} } sounds.clear(); nextAudio = context?.currentTime || 0; currentItem = null; itemSamples = 0;
  }
  function closeMedia() {
    clearPlayback(); capture?.disconnect(); source?.disconnect(); media?.getTracks().forEach(track => track.stop());
    if (context) context.close().catch(() => {}); capture = source = media = context = null;
  }
  function halt(message = 'Live microphone and playback stopped.') {
    generation++;
    active = ready = false; clearInterval(heartbeat); clearInterval(watchdog); pings.clear();
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'stop' }));
    if (socket && socket.readyState < WebSocket.CLOSING) socket.close();
    socket = null; closeMedia(); status.textContent = message; start.disabled = !capability?.ready; stop.disabled = interrupt.disabled = true;
    clearOffer(); modeControls.hidden = true; restartEveryday.hidden = true; startEveryday = false;
  }
  async function beginWindow(preparedTicket = null, preparedAt = null) {
    if (!active) return;
    const session = selectedSession(); const sentAt = preparedAt ?? performance.now();
    const ticket = preparedTicket ?? await api('/voice/tickets', { sessionId: session.id, deviceId, allowCloudProcessing: true });
    if (!active) return;
    leaseDeadline = sentAt + 15000; sequence = 0; pings.clear();
    const url = new URL('/voice-stream', location.href); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const ws = new WebSocket(url); socket = ws;
    ws.onopen = () => {
      if (active && socket === ws) {
        const frame = { type: 'authenticate', ticket: ticket.ticket, continuous: true };
        if (startEveryday) frame.startEveryday = true;
        startEveryday = false;
        ws.send(JSON.stringify(frame));
      }
      else ws.close();
    };
    ws.onmessage = async event => {
      if (socket !== ws || !active) return;
      try {
        const message = JSON.parse(event.data);
        if (message.type === 'ready') {
          windowNumber = Number.isInteger(message.window) ? message.window : 0;
          ready = true;
          const voice = typeof message.voiceName === 'string' && message.voiceName ? ` · voice ${message.voiceName}` : '';
          const mode = ['Everyday', 'Serious'].includes(message.mode) ? message.mode : '';
          status.textContent = `LIVE · microphone active${voice}${mode ? ` · ${mode} mode` : ''} · say a short answer or interrupt the reply.`;
          clearOffer(); modeControls.hidden = !mode; everyday.disabled = serious.disabled = false;
          modeNotice.textContent = message.recommendedMode === 'Serious' && mode === 'Everyday' ? 'Core recommends Serious voice for this activity.' : '';
          interrupt.disabled = false;
        }
        if (message.type === 'window_renewing') {
          ready = false;
          clearOffer(); everyday.disabled = serious.disabled = true; modeNotice.textContent = '';
          status.textContent = 'Renewing voice…';
        }
        if (message.type === 'voice_mode_offer' && ready && message.window === windowNumber &&
            typeof message.offerId === 'string' && Number.isFinite(message.maximumGbpForTenMinutes) && message.maximumGbpForTenMinutes > 0) {
          offerId = message.offerId;
          const maximum = (Math.ceil(message.maximumGbpForTenMinutes * 100) / 100).toFixed(2);
          modeNotice.textContent = `Serious voice reserves up to £${maximum} per 10 minutes. Confirm to start at the next renewal.`;
          confirmSerious.hidden = false;
        }
        if (message.type === 'voice_mode_queued' && ready && message.window === windowNumber) {
          clearOffer();
          modeNotice.textContent = `${message.mode} voice queued for the next renewal.`;
        }
        if (message.type === 'voice_mode_rejected' && ready && message.window === windowNumber) {
          clearOffer();
          modeNotice.textContent = message.detail;
        }
        if (message.type === 'lease') {
          const at = pings.get(message.sequence); if (at !== undefined) { leaseDeadline = at + message.leaseSeconds * 1000; pings.delete(message.sequence); }
        }
        if (message.type === 'audio' && ready && performance.now() < leaseDeadline) {
          const binary = atob(message.delta); if (binary.length % 2 || binary.length > 500000) throw Error('Invalid audio frame.');
          const pcm = new DataView(Uint8Array.from(binary, x => x.charCodeAt(0)).buffer); const buffer = context.createBuffer(1, binary.length / 2, 24000);
          const data = buffer.getChannelData(0); for (let i = 0; i < data.length; i++) data[i] = pcm.getInt16(i * 2, true) / 32768;
          const at = Math.max(context.currentTime + .02, nextAudio); if (at - context.currentTime > 8) throw Error('Playback queue is full; start again with shorter turns.');
          if (currentItem !== message.itemId) { currentItem = message.itemId; itemStart = at; itemSamples = 0; }
          itemSamples += data.length; const sound = context.createBufferSource(); sound.buffer = buffer; sound.connect(context.destination); sounds.add(sound); sound.onended = () => sounds.delete(sound); sound.start(at); nextAudio = at + buffer.duration;
        }
        if (message.type === 'interrupt') clearPlayback(true);
        if (message.type === 'transcript') {
          const line = document.createElement('p'); line.textContent = `${message.role === 'user' ? 'You' : 'JARVIS'}: ${message.text}`; transcript.append(line);
          while (transcript.children.length > 20) transcript.firstChild.remove();
        }
        if (message.type === 'error') {
          halt(message.detail);
          restartEveryday.hidden = message.code !== 'serious_voice_unavailable';
        }
        if (message.type === 'window_limit') { renewWanted = true; ready = false; clearPlayback(); ws.send(JSON.stringify({ type: 'stop' })); }
        if (message.type === 'ended') {
          ready = false; clearPlayback(); ws.onclose = null; ws.close();
          if (active && (message.reason === 'window_complete' || renewWanted)) { renewWanted = false; closeMedia(); status.textContent = 'Renewing the budgeted voice window…'; await prepareWindow(generation); }
          else halt('Voice stopped. Your completed transcript remains in Core.');
        }
      } catch (error) { halt(error.message || 'Voice protocol failed.'); }
    };
    ws.onerror = () => halt('Voice connection failed. Your completed transcript is saved; reconnect when ready.');
    ws.onclose = () => { if (active && socket === ws) halt('Voice disconnected. Microphone and playback stopped.'); };
  }
  async function prepareWindow(operation) {
    const session = selectedSession(), sentAt = performance.now();
    const ticket = await api('/voice/tickets', { sessionId: session.id, deviceId, allowCloudProcessing: true });
    try {
      if (generation !== operation || performance.now() - sentAt >= 12000) throw Error('Voice start was cancelled or expired.');
      const captured = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      if (generation !== operation || performance.now() - sentAt >= 12000) { captured.getTracks().forEach(track => track.stop()); throw Error('Voice start expired.'); }
      media = captured; context = new AudioContext({ sampleRate: 24000 }); await context.audioWorklet.addModule('/voice-capture.js');
      if (generation !== operation) throw Error('Voice start cancelled.');
      await context.resume(); source = context.createMediaStreamSource(media); capture = new AudioWorkletNode(context, 'jarvis-pcm'); source.connect(capture); capture.connect(context.destination);
      capture.port.onmessage = event => {
        if (!active || !ready || performance.now() >= leaseDeadline || socket?.readyState !== WebSocket.OPEN) return;
        if (socket.bufferedAmount > 256000) { halt('Network cannot keep up with live audio. Reconnect on a better connection.'); return; }
        const bytes = new Uint8Array(event.data); let binary = ''; for (const value of bytes) binary += String.fromCharCode(value);
        socket.send(JSON.stringify({ type: 'audio', window: windowNumber, audio: btoa(binary) }));
      };
      await beginWindow(ticket, sentAt);
    } catch (error) { closeMedia(); await api('/voice/local/release', ticket.lease).catch(() => {}); throw error; }
  }
  async function startLive(inEveryday = false) {
    if (!consent.checked) { notice('Approve cloud audio processing before starting live voice.', true); return; }
    start.disabled = restartEveryday.disabled = true;
    await beforeStart(); active = true; stop.disabled = false;
    startEveryday = inEveryday; restartEveryday.hidden = true; restartEveryday.disabled = false;
    const operation = ++generation;
    try {
      const session = await ensureSession('Communication', 'Live voice coaching');
      if (generation !== operation) return;
      await claimSession(session.id); if (generation !== operation) return;
      await prepareWindow(operation);
      heartbeat = setInterval(async () => {
        if (!active) return;
        if (socket?.readyState === WebSocket.OPEN) { const id = ++sequence; pings.set(id, performance.now()); socket.send(JSON.stringify({ type: 'heartbeat', sequence: id })); if (pings.size > 8) halt('Voice acknowledgements stopped.'); }
        try { await api(`/devices/${deviceId}/heartbeat`, {}); } catch { halt('Core is unreachable. Live audio stopped.'); }
      }, 4000);
      watchdog = setInterval(() => { if (active && performance.now() >= leaseDeadline) halt('Audio ownership expired. Reconnect to resume safely.'); }, 250);
    } catch (error) { if (generation === operation) { halt(error.message); notice(error.message, true); } }
  }
  start.addEventListener('click', () => startLive());
  restartEveryday.addEventListener('click', () => startLive(true));
  everyday.addEventListener('click', () => sendMode({ type: 'voice_mode', mode: 'Everyday' }));
  serious.addEventListener('click', () => sendMode({ type: 'voice_mode', mode: 'Serious' }));
  confirmSerious.addEventListener('click', () => { if (offerId) sendMode({ type: 'confirm_voice_mode', offerId }); });
  stop.addEventListener('click', () => halt()); interrupt.addEventListener('click', () => clearPlayback(true));
  consent.addEventListener('change', () => { if (!consent.checked) halt(); });
  return { stop: halt, async refresh() { capability = await api('/voice/status'); status.textContent = capability.detail; start.disabled = !capability.ready; }, clear() { halt(); consent.checked = false; transcript.replaceChildren(); capability = null; start.disabled = true; } };
}
