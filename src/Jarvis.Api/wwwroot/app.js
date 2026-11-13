const $ = id => document.getElementById(id);
const state = { key: '', session: null, devices: [], registered: false, recorder: null, stream: null, recognition: null, recordingTimer: null, pollBusy: false, releaseBusy: false };
const deviceId = localStorage.getItem('jarvis-device') || `browser-${crypto.randomUUID()}`;
localStorage.setItem('jarvis-device', deviceId);
$('device-id').textContent = `This browser: ${deviceId}`;
$('today').textContent = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric' }).format(new Date());
function notice(text, error = false) { $('notice').textContent = text; $('notice').className = error ? 'error' : ''; $('notice').hidden = false; }
async function api(path, body, method) {
  const multipart = body instanceof FormData;
  const response = await fetch(`/api${path}`, { method: method || (body === undefined ? 'GET' : 'POST'), headers: { 'X-Jarvis-Key': state.key, ...(!multipart && body !== undefined ? { 'Content-Type': 'application/json' } : {}) }, body: body === undefined ? undefined : multipart ? body : JSON.stringify(body) });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || data.detail || Object.values(data.errors || {}).flat().join(' ') || `Request failed (${response.status}).`);
  return data;
}
function on(id, event, fn) { $(id).addEventListener(event, async e => { e.preventDefault(); const button = e.submitter || (e.currentTarget.tagName === 'BUTTON' ? e.currentTarget : null); if (button) button.disabled = true; try { await fn(e); } catch (error) { notice(error.message, true); } finally { if (button) button.disabled = false; } }); }
function node(tag, text, className) { const el = document.createElement(tag); el.textContent = text; if (className) el.className = className; return el; }
function button(text, action) { const el = node('button', text); el.addEventListener('click', async () => { el.disabled = true; try { await action(); } catch (e) { notice(e.message, true); } finally { el.disabled = false; } }); return el; }
function selected() { if (!state.session) throw new Error('Open a conversation first.'); return state.session; }
function ownsAudio() { return state.session?.status === 'Active' && state.session?.activeDeviceId === deviceId; }
function stopAudio(discard = true) {
  if (state.recorder?.state === 'recording') { state.recorder.discard = discard; state.recorder.stop(); }
  clearTimeout(state.recordingTimer); state.stream?.getTracks().forEach(t => t.stop()); state.stream = null;
  if (state.recognition) { state.recognition.onend = null; state.recognition.stop(); state.recognition = null; }
  window.speechSynthesis?.cancel(); $('wake').textContent = 'Enable “Jarvis”'; $('record').textContent = 'Record voice';
}
document.querySelectorAll('[data-tab]').forEach(tab => tab.addEventListener('click', async () => {
  document.querySelectorAll('[data-tab]').forEach(t => t.classList.toggle('active', t === tab));
  document.querySelectorAll('[data-panel]').forEach(p => p.hidden = p.dataset.panel !== tab.dataset.tab);
  $('page-title').textContent = tab.textContent;
  if (!state.key) return;
  try { if (tab.dataset.tab === 'sessions') { await loadSessions(); await loadDevices(); } if (tab.dataset.tab === 'devices') await loadDevices(); if (tab.dataset.tab === 'learning') await loadLearning(); if (tab.dataset.tab === 'tools') await loadTools(); } catch (e) { notice(e.message, true); }
}));
on('connect-form', 'submit', async () => {
  state.key = $('api-key').value.trim();
  try { const integrations = await api('/integrations'); await loadSessions(); await loadDevices(); renderIntegrations(integrations); }
  catch (error) { state.key = ''; throw error; }
  $('api-key').value = ''; $('login').hidden = true; $('workspace').hidden = false; $('connection').textContent = 'Connected'; notice('Connected. Your saved sessions are ready.');
});
on('disconnect', 'click', () => { stopAudio(); state.key = ''; state.session = null; state.registered = false; $('workspace').hidden = true; $('login').hidden = false; $('connection').textContent = 'Disconnected'; $('notice').hidden = true; });
async function loadSessions() {
  const sessions = await api('/sessions'); $('session-count').textContent = sessions.length; $('session-list').replaceChildren();
  for (const s of sessions) { const b = button('', () => openSession(s.id)); b.className = 'session-button list-item' + (state.session?.id === s.id ? ' selected' : ''); b.append(node('strong', s.title), node('small', `${s.activity} · ${s.status}`)); $('session-list').append(b); }
  if (!sessions.length) $('session-list').append(node('p', 'Your conversations will appear here.', 'muted'));
}
async function openSession(id) { if (state.session?.id !== id) stopAudio(); state.session = await api(`/sessions/${id}`); renderSession(); await loadSessions(); }
function renderSession() {
  const s = state.session; if (!s) return;
  $('selected-title').textContent = s.title; $('session-activity').textContent = s.activity; $('session-status').textContent = s.status;
  ['session-controls', 'chat-form', 'checkpoint-panel'].forEach(id => $(id).hidden = false);
  $('chat-form').hidden = s.status !== 'Active';
  $('checkpoint-form').hidden = s.status === 'Ended';
  $('claim').hidden = s.status !== 'Active';
  $('pause').hidden = s.status !== 'Active';
  $('resume').hidden = s.status !== 'Paused';
  $('end').hidden = s.status === 'Ended';
  $('handoff-controls').hidden = s.status !== 'Active';
  $('owner').textContent = s.activeDeviceId ? `Active device: ${s.activeDeviceId === deviceId ? 'this browser' : s.activeDeviceId}` : 'No device owns audio. Choose “Use this device” to start voice.';
  const messages = s.messages || []; $('messages').replaceChildren();
  if (!messages.length) $('messages').append(node('p', s.status === 'Ended' ? 'Session ended. Your checkpoints are saved below.' : s.status === 'Paused' ? 'Session paused. Resume when you are ready.' : 'Your session is ready. Send a message, or save a checkpoint below.', 'muted'));
  for (const m of messages) { const div = node('div', '', `message ${m.role}`); div.append(node('small', m.role === 'assistant' ? 'JARVIS' : 'You'), document.createTextNode(m.text)); $('messages').append(div); }
  $('messages').scrollTop = $('messages').scrollHeight;
  $('checkpoints').replaceChildren(...s.checkpoints.slice().reverse().map(c => node('p', `${c.sequence}. ${c.summary}`, 'list-item')));
  const h = s.handoff; $('handoff-state').textContent = h ? `Transfer: ${h.state} · ${h.fromDeviceId} → ${h.toDeviceId}` : '';
  $('handoff-ready').hidden = !(h?.state === 'Checkpointed' && h.toDeviceId === deviceId);
  $('handoff-complete').hidden = !(h?.state === 'AudioTransferred' && h.toDeviceId === deviceId);
  $('handoff-cancel').hidden = !h || ['Completed', 'TimedOut', 'Failed'].includes(h.state);
  if (!ownsAudio()) stopAudio();
}
on('new-session', 'submit', async () => { const s = await api('/sessions', { activity: $('activity').value, title: $('session-title').value || null, deviceId: null }); $('session-title').value = ''; await openSession(s.id); });
on('claim', 'click', async () => { state.session = await api(`/sessions/${selected().id}/claim`, { deviceId }); renderSession(); });
for (const [id, status] of [['pause', 'Paused'], ['resume', 'Active'], ['end', 'Ended']]) on(id, 'click', async () => { stopAudio(); state.session = await api(`/sessions/${selected().id}/status`, { status }); renderSession(); await loadSessions(); });
on('checkpoint-form', 'submit', async () => { state.session = await api(`/sessions/${selected().id}/checkpoints`, { summary: $('checkpoint-summary').value, currentTask: $('checkpoint-task').value || null }); $('checkpoint-summary').value = ''; renderSession(); notice('Progress saved.'); });
on('chat-form', 'submit', async () => {
  const id = selected().id; const text = $('message').value; const requestId = crypto.randomUUID();
  const result = await api(`/sessions/${id}/messages`, { requestId, text });
  if (state.session?.id !== id) return;
  state.session = result; $('message').value = ''; renderSession();
  if ($('speak').checked && ownsAudio() && 'speechSynthesis' in window) { const utterance = new SpeechSynthesisUtterance(result.messages.at(-1).text); utterance.lang = result.activity === 'French' ? 'fr-FR' : 'en-GB'; window.speechSynthesis.speak(utterance); }
});
async function loadDevices() {
  state.devices = await api('/devices'); state.registered = state.devices.some(d => d.device.registration.deviceId === deviceId);
  $('device-list').replaceChildren(); const oldTarget = $('target-device').value; $('target-device').replaceChildren(node('option', 'Choose target device'));
  $('target-device').firstChild.value = '';
  for (const d of state.devices) { const r = d.device.registration; const div = node('div', '', 'list-item'); div.append(node('strong', r.name), node('p', `${r.deviceId} · ${d.online ? 'Online' : 'Offline'} · ${d.device.capabilitiesVerified ? 'Verified' : 'Needs capability test'}`, 'muted')); $('device-list').append(div);
    if (r.deviceId !== deviceId && d.online && d.device.capabilitiesVerified) { const option = node('option', r.name); option.value = r.deviceId; $('target-device').append(option); }
  } $('target-device').value = oldTarget;
}
on('device-form', 'submit', async () => { await api('/devices', { deviceId, name: $('device-name').value, kind: /Quest|Oculus/i.test(navigator.userAgent) ? 'QuestClient' : /Android/i.test(navigator.userAgent) ? 'AndroidCompanion' : 'WindowsAgent', capabilities: { canListen: !!navigator.mediaDevices?.getUserMedia, canSpeak: 'speechSynthesis' in window, hasDisplay: true, canControlHome: false, extra: ['browser-companion'] } }); await loadDevices(); notice('Registered. Test microphone and speaker to verify this device.'); });
on('test-device', 'click', async () => {
  if (!state.registered) throw new Error('Register this browser first.');
  if (!navigator.mediaDevices?.getUserMedia || !('speechSynthesis' in window)) throw new Error('Microphone and speech output need a supported browser on HTTPS or localhost.');
  const stream = await navigator.mediaDevices.getUserMedia({ audio: true }); stream.getTracks().forEach(t => t.stop());
  window.speechSynthesis.speak(new SpeechSynthesisUtterance('JARVIS speaker test. If you can hear me, confirm the test.'));
  notice('Microphone access passed. After hearing the speaker test, confirm below.');
  const confirm = button('I heard the speaker test — verify this device', async () => { await api(`/devices/${deviceId}/verify`, {}); confirm.remove(); await loadDevices(); notice('Device verified. You can claim a session or receive a handoff.'); }); $('device-list').prepend(confirm);
});
on('handoff', 'click', async () => { const target = $('target-device').value; if (!target) throw new Error('Choose an online, verified target device.'); const summary = $('checkpoint-summary').value || selected().currentTask || 'Continue the current conversation and saved transcript.'; state.session = await api(`/sessions/${selected().id}/handoff`, { fromDeviceId: deviceId, toDeviceId: target, summary }); renderSession(); });
for (const [id, action] of [['handoff-ready', 'ready'], ['handoff-complete', 'complete'], ['handoff-cancel', 'cancel']]) on(id, 'click', async () => { state.session = await api(`/sessions/${selected().id}/handoff/${action}`, { handoffId: selected().handoff.id, deviceId }); renderSession(); });
async function loadLearning() {
  const cards = await api('/learning?due=true'); $('learning-list').replaceChildren();
  if (!cards.length) $('learning-list').append(node('p', 'All caught up. Add a card or come back when one is due.', 'muted'));
  for (const card of cards) { const div = node('div', '', 'list-item'); const started = Date.now(); div.append(node('small', card.domain), node('p', card.prompt)); const answer = node('p', card.answer); answer.hidden = true; const ratings = node('div', '', 'row wrap'); ratings.hidden = true; for (const rating of ['Again', 'Hard', 'Good', 'Easy']) ratings.append(button(rating, async () => { await api(`/learning/${card.id}/reviews`, { requestId: crypto.randomUUID(), rating, responseSeconds: Math.min(3600, (Date.now() - started) / 1000) }); await loadLearning(); })); div.append(button('Reveal answer', () => { answer.hidden = false; ratings.hidden = false; }), answer, ratings); $('learning-list').append(div); }
}
on('learning-form', 'submit', async () => { await api('/learning', { domain: $('learning-domain').value, prompt: $('learning-prompt').value, answer: $('learning-answer').value }); $('learning-prompt').value = ''; $('learning-answer').value = ''; await loadLearning(); notice('Learning card added.'); });
on('refresh-learning', 'click', loadLearning);
on('refresh-briefing', 'click', async () => { notice('Loading live sources…'); const briefing = await api('/briefing'); $('briefing-content').replaceChildren(); for (const section of briefing.sections) { const div = node('div', '', 'card'); div.append(node('h2', section.name)); if (!section.available) div.append(node('p', section.error, 'muted')); else if (!section.data.length) div.append(node('p', 'Nothing scheduled in this window.', 'muted')); else for (const item of section.data) div.append(node('p', item.name ? `${item.name}: ${item.status.join('; ')}` : item.title ? `${item.title}\n${item.start} → ${item.end}` : `${item.domain}: ${item.prompt}`, 'list-item')); $('briefing-content').append(div); } notice(`Updated ${new Date(briefing.generatedAt).toLocaleTimeString()}.`); });
async function loadTools() { const tools = await api('/tools'); $('tool-list').replaceChildren(); if (!tools.length) $('tool-list').append(node('p', 'No actions yet.', 'muted')); for (const tool of tools) { const div = node('div', '', 'list-item'); const invocation = tool.invocation; const args = JSON.parse(invocation.argumentsJson); div.append(node('strong', `${args.action} · ${args.entityId}`), node('p', invocation.status), node('small', tool.result || 'Awaiting your decision.')); if (invocation.status === 'AwaitingConfirmation') for (const approve of [true, false]) div.append(button(approve ? 'Confirm action' : 'Reject', async () => { await api(`/tools/${invocation.id}/confirm`, { approve }); await loadTools(); })); $('tool-list').append(div); } }
on('home-form', 'submit', async () => { await api('/tools/home', { requestId: crypto.randomUUID(), sessionId: selected().id, entityId: $('home-entity').value, action: $('home-action').value }); await loadTools(); notice('Action prepared. Review the entity and action, then confirm.'); });
on('refresh-tools', 'click', loadTools);
function renderIntegrations(integrations) { $('integration-list').replaceChildren(...integrations.map(i => { const div = node('div', '', 'list-item'); div.append(node('strong', `${i.name} · ${i.configured ? 'Configured' : 'Needs setup'}`), node('p', i.detail, 'muted')); return div; })); }
on('record', 'click', async () => {
  if (state.recorder?.state === 'recording') { stopAudio(false); return; }
  if (!ownsAudio()) throw new Error('Verify this device, then choose “Use this device” before recording.');
  if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) throw new Error('Recording needs a supported browser on HTTPS or localhost.');
  stopAudio(); const sessionId = selected().id; state.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
  if (!ownsAudio() || state.session.id !== sessionId) { stopAudio(); return; }
  const chunks = []; const recorder = new MediaRecorder(state.stream); state.recorder = recorder; let size = 0;
  recorder.ondataavailable = e => { size += e.data.size; chunks.push(e.data); if (size > 9_000_000) { stopAudio(); notice('Recording exceeded the size limit.', true); } };
  recorder.onstop = async () => { if (recorder.discard || !ownsAudio() || state.session?.id !== sessionId) return; try { const form = new FormData(); const type = recorder.mimeType; form.append('audio', new Blob(chunks, { type }), type.includes('mp4') ? 'recording.mp4' : 'recording.webm'); notice('Transcribing…'); const transcript = await api('/voice/transcribe', form); if (ownsAudio() && state.session?.id === sessionId) { $('message').value = transcript.text; notice('Transcript ready. Review it, then send.'); } } catch (e) { notice(e.message, true); } };
  recorder.start(1000); $('record').textContent = 'Stop & transcribe'; state.recordingTimer = setTimeout(() => stopAudio(false), 60000); notice('Recording. Stops automatically after one minute.');
});
on('stop-voice', 'click', () => { stopAudio(); notice('Microphone and speech output stopped.'); });
on('wake', 'click', () => {
  if (state.recognition) { stopAudio(); return; }
  if (!ownsAudio()) throw new Error('Claim this session on a verified device before enabling the wake word.');
  const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition; if (!Recognition) throw new Error('Wake-word mode is not supported by this browser. Use Record voice.');
  stopAudio(); const recognition = new Recognition(); state.recognition = recognition; recognition.continuous = true; recognition.lang = 'en-GB';
  recognition.onresult = e => { if (!ownsAudio()) { stopAudio(); return; } for (let i = e.resultIndex; i < e.results.length; i++) { if (!e.results[i].isFinal) continue; const text = e.results[i][0].transcript; const match = /\bjarvis\b[,.]?\s*(.*)/i.exec(text); if (match) { $('message').value = match[1]; notice('Heard “Jarvis”. Review your message before sending.'); } } };
  recognition.onerror = e => { stopAudio(); notice(`Speech recognition stopped: ${e.error}`, true); };
  recognition.onend = () => { state.recognition = null; $('wake').textContent = 'Enable “Jarvis”'; };
  recognition.start(); $('wake').textContent = 'Disable “Jarvis”'; notice('Listening for “Jarvis” while this page is visible. Your browser may send speech to its recognition provider.');
});
document.addEventListener('visibilitychange', () => { if (document.hidden) stopAudio(); });
window.addEventListener('pagehide', () => stopAudio());
setInterval(async () => { if (!state.key || !state.registered) return; try { await api(`/devices/${deviceId}/heartbeat`, {}); await loadDevices(); } catch { stopAudio(); $('connection').textContent = 'Connection lost'; } }, 15000);
setInterval(async () => {
  if (!state.key || !state.session || state.pollBusy) return; state.pollBusy = true;
  try { const id = state.session.id; const latest = await api(`/sessions/${id}`); if (state.session?.id !== id) return;
    if (JSON.stringify(latest) !== JSON.stringify(state.session)) { state.session = latest; renderSession(); }
    if (latest.handoff?.state === 'TargetReady' && latest.handoff.fromDeviceId === deviceId && !state.releaseBusy) { state.releaseBusy = true; try { stopAudio(); state.session = await api(`/sessions/${id}/handoff/release`, { handoffId: latest.handoff.id, deviceId }); renderSession(); } finally { state.releaseBusy = false; } }
    $('connection').textContent = 'Connected';
  } catch { stopAudio(); $('connection').textContent = 'Connection lost'; } finally { state.pollBusy = false; }
}, 3000);
