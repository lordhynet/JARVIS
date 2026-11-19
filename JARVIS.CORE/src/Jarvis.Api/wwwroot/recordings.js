export function initRecordings({ api, node, button, notice }) {
  const tab = node('button', 'Interview recordings'); tab.dataset.tab = 'recordings'; document.querySelector('nav').append(tab);
  const panel = node('section'); panel.dataset.panel = 'recordings'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'), list = node('div', '', 'list'), editor = node('div'), status = node('p', '', 'muted'); panel.append(card);
  card.append(node('h2', 'Record an interview'), node('p', 'Prepare a recording for your enrolled phone or laptop, then confirm and start on that unlocked device. Keep the recording indicator visible. Phone capture is for face-to-face conversations; use a laptop for call audio. Nothing is transcribed automatically.'), status, editor, list, button('Refresh recordings', load));
  let generation = 0, pending = null;
  function field(form, title, type = 'text') { const label = node('label', title), input = node('input'); input.type = type; label.append(input); form.append(label); return input; }
  function check(form, title) { const label = node('label', '', 'inline'), input = node('input'); input.type = 'checkbox'; label.append(input, document.createTextNode(title)); form.append(label); return input; }
  function requireVisible() { if (document.hidden) throw Error('Open this page on your unlocked device before managing recordings.'); }
  async function load() {
    requireVisible(); const token = ++generation;
    const [settings, recordings, devices] = await Promise.all([api('/recordings/status'), api('/recordings?locked=false'), api('/devices')]);
    if (token !== generation || document.hidden) return;
    status.textContent = settings.ready ? 'Encrypted audio is kept for 30 days. Recordings and transcripts stay private. Choose deletion after transcription if you only need the text.' : 'Encrypted recording storage is not configured. Your server operator must enable it before you can prepare a recording.';
    editor.replaceChildren();
    if (settings.ready) {
      const form = node('form'), title = field(form, 'Interview name'); title.required = true; title.maxLength = 120;
      const label = node('label', 'Capture device'), select = node('select'); label.append(select); form.append(label);
      for (const view of devices) { const registration = view.device.registration;
        if (!['AndroidCompanion', 'WindowsAgent'].includes(registration.kind) || registration.capabilities.extra.includes('browser-companion') || !registration.capabilities.canListen) continue;
        const option = node('option', registration.name); option.value = registration.deviceId; select.append(option);
      }
      const storage = check(form, 'I agree to store this recording for my private practice.'), authorised = check(form, settings.consent);
      const remove = check(form, 'Delete the audio after a confirmed transcription.');
      const submit = node('button', 'Prepare recording', 'primary'); submit.type = 'submit'; submit.disabled = !select.options.length; form.append(submit);
      form.addEventListener('submit', async event => {
        event.preventDefault(); submit.disabled = true; const current = generation;
        try {
          requireVisible(); if (!storage.checked || !authorised.checked) throw Error('Confirm storage and recording authorization for this interview.');
          const body = { deviceId: select.value, title: title.value.trim(), confirmStorageConsent: storage.checked, confirmRecordingAuthorised: authorised.checked, deleteAudioAfterTranscription: remove.checked, locked: false };
          const fingerprint = JSON.stringify(body); if (pending?.fingerprint !== fingerprint) pending = { fingerprint, id: crypto.randomUUID() };
          await api('/recordings', { ...body, requestId: pending.id });
          if (current !== generation || document.hidden) return; pending = null; await load(); notice('Recording prepared. Confirm and start it on the selected device.');
        } catch (error) { if (current === generation) notice(error.message, true); } finally { submit.disabled = !select.options.length; }
      }); editor.append(form);
    }
    list.replaceChildren(node('h3', 'Your recordings'));
    if (!recordings.length) list.append(node('p', 'No interviews recorded yet.', 'muted'));
    for (const recording of recordings) {
      const row = node('div', '', 'list-item'); row.append(node('h3', recording.title), node('p', `${recording.status} · ${Math.round(recording.durationSamples / 16000)} seconds · ${recording.audioDeleted ? 'Audio removed' : 'Encrypted audio'}`));
      if (recording.status === 'Incomplete') row.append(node('p', 'Some audio is missing or capture was interrupted. This is not a complete interview.', 'muted'));
      if (recording.audioRetainUntil && !recording.audioDeleted) row.append(node('small', `Audio retention ends ${new Date(recording.audioRetainUntil).toLocaleString()}.`));
      row.append(button('Delete recording', async () => {
        requireVisible(); if (!confirm('Delete this recording and its audio? Linked transcript and derived practice will also be removed.')) return;
        const current = generation; await api(`/recordings/${recording.id}/delete`, { revision: recording.revision, locked: false });
        if (current === generation && !document.hidden) { await load(); notice('Recording deleted.'); }
      })); list.append(row);
    }
  }
  function clear() { generation++; pending = null; editor.replaceChildren(); list.replaceChildren(); status.textContent = ''; }
  document.addEventListener('visibilitychange', () => { if (document.hidden) clear(); });
  return { load, clear };
}
