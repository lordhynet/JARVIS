import { initModules } from './modules.js';
import { initLiveVoice } from './live.js';
import { initIntelligence } from './intelligence.js';
import { initPrivacy } from './privacy.js';
import { initPlanning } from './planning.js';
import { initImports } from './imports.js';
import { initNotifications } from './notifications.js';
let liveVoice;
let connectionGeneration = 0;
const $ = id => document.getElementById(id);
const state = { key: '', session: null, devices: [], registered: false, recorder: null, stream: null, recognition: null, recordingTimer: null, pollBusy: false, releaseBusy: false };
const deviceId = localStorage.getItem('jarvis-device') || `browser-${crypto.randomUUID()}`;
localStorage.setItem('jarvis-device', deviceId);
$('device-id').textContent = `This browser: ${deviceId}`;
$('today').textContent = new Intl.DateTimeFormat(undefined, { weekday: 'long', month: 'long', day: 'numeric' }).format(new Date());
function notice(text, error = false) { $('notice').textContent = text; $('notice').className = error ? 'error' : ''; $('notice').hidden = false; }
async function api(path, body, method) {
  const generation = connectionGeneration;
  const multipart = body instanceof FormData;
  const verb = method || (body === undefined ? 'GET' : 'POST');
  const ownerCall = verb === 'GET' && path === '/privacy/export' ||
    verb === 'POST' && ['/privacy/erase', '/privacy/reopen', '/privacy/retention/apply'].includes(path) ||
    ['POST', 'DELETE'].includes(verb) && /^\/devices\/[^/]+\/(verify|credential)$/.test(path) ||
    verb === 'POST' && /^\/devices\/[^/]+\/grants$/.test(path) ||
    ['GET', 'DELETE'].includes(verb) && /^\/device-grants(?:\/[^/]+)?$/.test(path) ||
    verb === 'POST' && /^\/device-commands\/[^/]+\/confirm$/.test(path) ||
    verb === 'POST' && /^\/agents\/runs\/[^/]+\/(tool-grants|release)$/.test(path) ||
    verb === 'POST' && /^\/agents\/actions\/[^/]+\/approve$/.test(path) ||
    verb === 'DELETE' && /^\/agents\/tool-grants\/[^/]+$/.test(path) ||
    verb === 'POST' && /^\/domains\/[^/]+\/(activate|disable|rollback)$/.test(path) ||
    verb === 'POST' && /^\/tools\/[^/]+\/confirm$/.test(path) ||
    verb === 'POST' && /^\/budget\/(approvals(?:\/[^/]+\/revoke)?|legacy\/[^/]+\/classify)$/.test(path) ||
    verb === 'GET' && /^\/budget\/legacy\/[^/]+\/preview$/.test(path) ||
    verb === 'POST' && /^\/usage\/charges\/[^/]+\/reconcile$/.test(path);
  const response = await fetch(`/api${path}`, { method: verb, headers: { 'X-Jarvis-Key': state.key, ...(ownerCall && $('owner-key').value ? { 'X-Jarvis-Owner-Key': $('owner-key').value } : {}), ...(!multipart && body !== undefined ? { 'Content-Type': 'application/json' } : {}) }, body: body === undefined ? undefined : multipart ? body : JSON.stringify(body) });
  const data = await response.json().catch(() => ({}));
  if (generation !== connectionGeneration) throw new Error('This response belongs to a previous Core connection.');
  if (!response.ok) throw new Error(data.error || data.detail || Object.values(data.errors || {}).flat().join(' ') || `Request failed (${response.status}).`);
  return data;
}
function on(id, event, fn) { $(id).addEventListener(event, async e => { e.preventDefault(); const button = e.submitter || (e.currentTarget.tagName === 'BUTTON' ? e.currentTarget : null); if (button) button.disabled = true; try { await fn(e); } catch (error) { notice(error.message, true); } finally { if (button) button.disabled = false; } }); }
function node(tag, text, className) { const el = document.createElement(tag); el.textContent = text; if (className) el.className = className; return el; }
function button(text, action) { const el = node('button', text); el.type = 'button'; el.addEventListener('click', async () => { el.disabled = true; try { await action(); } catch (e) { notice(e.message, true); } finally { el.disabled = false; } }); return el; }
function selected() { if (!state.session) throw new Error('Open a conversation first.'); return state.session; }
function ownsAudio() { return state.session?.status === 'Active' && state.session?.activeDeviceId === deviceId; }
let localAudio = null, localDeadline = 0, audioGeneration = 0, localRelease = Promise.resolve();
function releaseLocalAudio() {
  const lease = localAudio; localAudio = null; localDeadline = 0;
  if (lease) localRelease = localRelease.then(() => api('/voice/local/release', lease)).catch(() => {});
}
async function acquireLocalAudio(sessionId, calibration = false) {
  const generation = audioGeneration; await localRelease; const started = performance.now();
  const lease = await api(calibration ? '/voice/calibrate' : '/voice/local', { sessionId, deviceId, allowCloudProcessing: false });
  if (generation !== audioGeneration || performance.now() - started >= 12000) { await api('/voice/local/release', lease).catch(() => {}); throw new Error('Audio start was cancelled or expired.'); }
  localAudio = lease; localDeadline = started + 12000;
}
setInterval(async () => {
  const lease = localAudio; if (!lease) return;
  const started = performance.now();
  try { const renewed = await api('/voice/local/renew', lease); if (localAudio?.fence === lease.fence && performance.now() - started < 12000) { localAudio = renewed; localDeadline = started + 12000; } }
  catch { if (localAudio?.fence === lease.fence) { stopAudio(); notice('Audio ownership expired. Start again when Core is reachable.', true); } }
}, 4000);
setInterval(() => { if (localAudio && performance.now() >= localDeadline) { stopAudio(); notice('Audio lease expired; microphone and playback stopped.', true); } }, 250);
function stopAudio(discard = true) {
  audioGeneration++; releaseLocalAudio();
  liveVoice?.stop();
  if (state.recorder?.state === 'recording') { state.recorder.discard = discard; state.recorder.stop(); }
  clearTimeout(state.recordingTimer); state.stream?.getTracks().forEach(t => t.stop()); state.stream = null;
  if (state.recognition) { state.recognition.onend = null; state.recognition.stop(); state.recognition = null; }
  window.speechSynthesis?.cancel(); $('wake').textContent = 'Enable cloud keyword listening'; $('record').textContent = 'Record voice';
}
const extraModules = initModules({ api, notice, node, button, on, selectedSession: () => state.session,
  ensureSession: async (activity, title) => { if (state.session && state.session.status !== 'Ended') return state.session; const session = await api('/sessions', { activity, title, deviceId: null }); await openSession(session.id); return state.session; } });
liveVoice = initLiveVoice({ api, selectedSession: () => state.session, deviceId, notice, beforeStart: async () => { stopAudio(); await localRelease; },
  claimSession: async id => { state.session = await api(`/sessions/${id}/claim`, { deviceId }); renderSession(); },
  ensureSession: async (activity, title) => { if (state.session && state.session.status !== 'Ended') return state.session; const session = await api('/sessions', { activity, title, deviceId: null }); await openSession(session.id, true); return state.session; } });
const intelligence = initIntelligence({ api, node, button, notice });
const notifications = initNotifications({ api, node, button, notice });
const imports = initImports({ api, node, button, notice });
const planning = initPlanning({ api, node, button, notice });
const privacy = initPrivacy({ api, node, button, notice, onErased: () => {
  connectionGeneration++;
  stopAudio(); liveVoice.clear(); intelligence.clear(); extraModules.clear(); planning.clear(); imports.clear(); notifications.clear(); state.session = null; state.devices = []; state.registered = false;
  for (const id of ['session-list', 'device-list', 'learning-list', 'tool-list', 'device-command-list', 'messages', 'checkpoints', 'briefing-content', 'integration-list']) $(id)?.replaceChildren();
  for (const field of document.querySelectorAll('[data-panel] input,[data-panel] textarea')) { if (field.type === 'checkbox' || field.type === 'radio') field.checked = false; else field.value = ''; }
  for (const id of ['selected-title', 'owner', 'handoff-state']) $(id).textContent = '';
  for (const id of ['session-controls', 'chat-form', 'checkpoint-panel', 'handoff-controls']) $(id).hidden = true;
  $('session-count').textContent = '0';
  $('connection').textContent = 'Core erased';
}, onReopened: async () => { await loadSessions(); await loadDevices(); renderIntegrations(await api('/integrations')); $('connection').textContent = 'Connected'; } });
document.querySelectorAll('[data-tab]').forEach(tab => tab.addEventListener('click', async () => {
  document.querySelectorAll('[data-tab]').forEach(t => t.classList.toggle('active', t === tab));
  document.querySelectorAll('[data-panel]').forEach(p => p.hidden = p.dataset.panel !== tab.dataset.tab);
  $('page-title').textContent = tab.textContent;
  if (!state.key) return;
  try { if (tab.dataset.tab === 'sessions') { await loadSessions(); await loadDevices(); } if (tab.dataset.tab === 'devices') await loadDevices(); if (tab.dataset.tab === 'learning') await loadLearning(); if (tab.dataset.tab === 'tools') await loadTools(); if (tab.dataset.tab === 'agents') await intelligence.load(); if (tab.dataset.tab === 'privacy') await privacy.load(); if (tab.dataset.tab === 'planning') await planning.load(); if (tab.dataset.tab === 'imports') await imports.load(); await extraModules.load(tab.dataset.tab); } catch (e) { notice(e.message, true); }
}));
on('connect-form', 'submit', async () => {
  connectionGeneration++;
  state.key = $('api-key').value.trim();
  try {
    const control = await api('/privacy/status');
    if (control.erased) {
      $('api-key').value = ''; $('login').hidden = true; $('workspace').hidden = false; $('connection').textContent = 'Core erased'; privacy.show(); return;
    }
    const integrations = await api('/integrations'); await loadSessions(); await loadDevices(); renderIntegrations(integrations);
  }
  catch (error) { state.key = ''; throw error; }
  $('api-key').value = ''; $('login').hidden = true; $('workspace').hidden = false; $('connection').textContent = 'Connected'; notice('Connected. Your saved sessions are ready.');
  await liveVoice.refresh();
});
on('disconnect', 'click', () => { connectionGeneration++; stopAudio(); document.getElementById('verify-device')?.remove(); liveVoice.clear(); intelligence.clear(); extraModules.clear(); planning.clear(); imports.clear(); notifications.clear(); privacy.clear(); state.key = ''; $('owner-key').value = ''; state.session = null; state.registered = false; $('workspace').hidden = true; $('login').hidden = false; $('connection').textContent = 'Disconnected'; $('notice').hidden = true; });
async function loadSessions() {
  const sessions = await api('/sessions'); $('session-count').textContent = sessions.length; $('session-list').replaceChildren();
  for (const s of sessions) { const b = button('', () => openSession(s.id)); b.className = 'session-button list-item' + (state.session?.id === s.id ? ' selected' : ''); b.append(node('strong', s.title), node('small', `${s.activity} · ${s.status}`)); $('session-list').append(b); }
  if (!sessions.length) $('session-list').append(node('p', 'Your conversations will appear here.', 'muted'));
}
async function openSession(id, keepLive = false) { if (state.session?.id !== id && !keepLive) stopAudio(); state.session = await api(`/sessions/${id}`); extraModules.sessionChanged(id); renderSession(keepLive); await loadSessions(); }
function renderSession(keepLive = false) {
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
  for (const m of messages) { const div = node('div', '', `message ${m.role}`); div.append(node('small', `${m.role === 'assistant' ? 'JARVIS' : 'You'}${m.restricted ? ' · Locked device — audit only' : ''}`), document.createTextNode(m.text)); $('messages').append(div); }
  $('messages').scrollTop = $('messages').scrollHeight;
  $('checkpoints').replaceChildren(...s.checkpoints.slice().reverse().map(c => node('p', `${c.sequence}. ${c.summary}`, 'list-item')));
  const h = s.handoff; $('handoff-state').textContent = h ? `Transfer: ${h.state} · ${h.fromDeviceId} → ${h.toDeviceId}` : '';
  $('handoff-ready').hidden = !(h?.state === 'Checkpointed' && h.toDeviceId === deviceId);
  $('handoff-complete').hidden = !(h?.state === 'AudioTransferred' && h.toDeviceId === deviceId);
  $('handoff-cancel').hidden = !h || ['Completed', 'TimedOut', 'Failed'].includes(h.state);
  if (!ownsAudio() && !keepLive) stopAudio();
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
  if ($('speak').checked && ownsAudio() && 'speechSynthesis' in window) {
    stopAudio(); await acquireLocalAudio(id); const fence = localAudio.fence;
    try {
      const utterance = new SpeechSynthesisUtterance(result.messages.at(-1).text); utterance.lang = result.activity === 'French' ? 'fr-FR' : 'en-GB';
      utterance.onend = utterance.onerror = () => { if (localAudio?.fence === fence) releaseLocalAudio(); }; window.speechSynthesis.speak(utterance);
    } catch (error) { if (localAudio?.fence === fence) stopAudio(); throw error; }
  }
});
async function loadDevices() {
  state.devices = await api('/devices'); state.registered = state.devices.some(d => d.device.registration.deviceId === deviceId);
  $('test-device').disabled = !state.registered;
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
  const connectionKey = state.key;
  stopAudio(); await acquireLocalAudio('00000000-0000-0000-0000-000000000000', true);
  try { const stream = await navigator.mediaDevices.getUserMedia({ audio: true }); stream.getTracks().forEach(t => t.stop()); }
  catch (error) { stopAudio(); throw error; }
  if (!state.key || connectionKey !== state.key) { stopAudio(); return; }
  const speaker = new SpeechSynthesisUtterance('JARVIS speaker test. If you can hear me, confirm the test.');
  const speakerFence = localAudio?.fence; speaker.onend = speaker.onerror = () => { if (localAudio?.fence === speakerFence) releaseLocalAudio(); };
  try { window.speechSynthesis.speak(speaker); } catch (error) { stopAudio(); throw error; }
  const calibrationFence = localAudio?.fence; setTimeout(() => { if (localAudio?.fence === calibrationFence) releaseLocalAudio(); }, 10000);
  notice('Microphone access passed. After hearing the speaker test, confirm below.');
  document.getElementById('verify-device')?.remove();
  const confirm = button('I heard the speaker test — verify this device', async () => { stopAudio(); await localRelease; await api(`/devices/${deviceId}/verify`, {}); confirm.remove(); await loadDevices(); notice('Device verified. You can claim a session or receive a handoff.'); });
  confirm.id = 'verify-device'; $('device-form').after(confirm);
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
async function loadTools() {
  const [tools, commands] = await Promise.all([api('/tools'), api('/device-commands')]);
  const history = $('tool-list');
  history.replaceChildren();
  if (!tools.length) history.append(node('p', 'No actions yet.', 'muted'));
  for (const tool of tools) {
    const div = node('div', '', 'list-item');
    const invocation = tool.invocation;
    const args = JSON.parse(invocation.argumentsJson);
    div.append(node('strong', `${args.action} · ${args.entityId}`), node('p', invocation.status), node('small', tool.result || 'Awaiting your decision.'));
    if (invocation.status === 'AwaitingConfirmation') for (const approve of [true, false])
      div.append(button(approve ? 'Confirm action' : 'Reject', async () => {
        await api(`/tools/${invocation.id}/confirm`, { approve });
        await loadTools();
      }));
    history.append(div);
  }
  renderDeviceCommands(commands);
}

function renderDeviceCommands(commands) {
  const list = $('device-command-list');
  list.replaceChildren();
  if (!commands.length) list.append(node('p', 'No device commands yet.', 'muted'));
  for (const command of commands) {
    const row = node('div', '', 'list-item');
    row.append(node('strong', command.capabilityId),
      node('p', `${command.status} · ${command.origin} · ${command.deviceId}`),
      node('pre', command.argumentsJson, 'device-command-arguments'));
    if (command.status === 'AwaitingConfirmation') {
      row.append(node('small', `Expires ${new Date(command.expiresAt).toLocaleTimeString()}`));
      const decisions = node('div', '', 'actions');
      for (const approve of [true, false]) {
        decisions.append(button(approve ? 'Approve device command' : 'Reject device command', async () => {
          for (const control of decisions.children) control.disabled = true;
          try {
            await api(`/device-commands/${encodeURIComponent(command.id)}/confirm`, { argumentHash: command.argumentHash, approve });
            await loadTools();
          } finally {
            for (const control of decisions.children) control.disabled = false;
          }
        }));
      }
      row.append(decisions);
    }
    list.append(row);
  }
}
on('home-form', 'submit', async () => { await api('/tools/home', { requestId: crypto.randomUUID(), sessionId: selected().id, entityId: $('home-entity').value, action: $('home-action').value }); await loadTools(); notice('Action prepared. Review the entity and action, then confirm.'); });
on('refresh-tools', 'click', loadTools);
function renderIntegrations(integrations) { $('integration-list').replaceChildren(...integrations.map(i => { const div = node('div', '', 'list-item'); div.append(node('strong', `${i.name} · ${i.configured ? 'Configured' : 'Needs setup'}`), node('p', i.detail, 'muted')); return div; })); }
on('record', 'click', async () => {
  if (state.recorder?.state === 'recording') { stopAudio(false); return; }
  if (!ownsAudio()) throw new Error('Verify this device, then choose “Use this device” before recording.');
  if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) throw new Error('Recording needs a supported browser on HTTPS or localhost.');
  stopAudio(); const sessionId = selected().id; await acquireLocalAudio(sessionId); const recordFence = localAudio.fence;
  try { state.stream = await navigator.mediaDevices.getUserMedia({ audio: true }); } catch (error) { stopAudio(); throw error; }
  if (!ownsAudio() || state.session.id !== sessionId || localAudio?.fence !== recordFence || performance.now() >= localDeadline) { stopAudio(); return; }
  const chunks = []; const recorder = new MediaRecorder(state.stream); state.recorder = recorder; let size = 0;
  recorder.ondataavailable = e => { size += e.data.size; chunks.push(e.data); if (size > 9_000_000) { stopAudio(); notice('Recording exceeded the size limit.', true); } };
  recorder.onstop = async () => { if (recorder.discard || !ownsAudio() || state.session?.id !== sessionId) return; try { const form = new FormData(); const type = recorder.mimeType; form.append('audio', new Blob(chunks, { type }), type.includes('mp4') ? 'recording.mp4' : 'recording.webm'); notice('Transcribing…'); const transcript = await api('/voice/transcribe', form); if (ownsAudio() && state.session?.id === sessionId) { $('message').value = transcript.text; notice('Transcript ready. Review it, then send.'); } } catch (e) { notice(e.message, true); } };
  recorder.start(1000); $('record').textContent = 'Stop & transcribe'; state.recordingTimer = setTimeout(() => stopAudio(false), 60000); notice('Recording. Stops automatically after one minute.');
});
on('stop-voice', 'click', () => { stopAudio(); notice('Microphone and speech output stopped.'); });
on('wake', 'click', async () => {
  if (state.recognition) { stopAudio(); return; }
  if (!ownsAudio()) throw new Error('Claim this session on a verified device before enabling cloud keyword listening.');
  const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition; if (!Recognition) throw new Error('Cloud speech recognition is not supported by this browser. Use Record voice.');
  if (!window.confirm('Enable cloud speech recognition? Your browser may continuously send microphone audio to its speech provider while this page is visible. This is not local Hey JARVIS detection.')) return;
  stopAudio(); await acquireLocalAudio(selected().id); const recognition = new Recognition(); state.recognition = recognition; recognition.continuous = true; recognition.lang = 'en-GB';
  recognition.onresult = e => { if (!ownsAudio()) { stopAudio(); return; } for (let i = e.resultIndex; i < e.results.length; i++) { if (!e.results[i].isFinal) continue; const text = e.results[i][0].transcript; const match = /\bjarvis\b[,.]?\s*(.*)/i.exec(text); if (match) { $('message').value = match[1]; notice('Heard “Jarvis”. Review your message before sending.'); } } };
  recognition.onerror = e => { stopAudio(); notice(`Speech recognition stopped: ${e.error}`, true); };
  recognition.onend = () => { if (state.recognition !== recognition) return; state.recognition = null; releaseLocalAudio(); $('wake').textContent = 'Enable cloud keyword listening'; };
  try { recognition.start(); } catch (error) { stopAudio(); throw error; } $('wake').textContent = 'Disable cloud keyword listening'; notice('Cloud keyword listening is active while this page is visible. Your browser may send microphone audio to its speech provider.');
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
