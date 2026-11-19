export function initRecordings({ api, node, button, notice }) {
  const tab = node('button', 'Interview recordings'); tab.dataset.tab = 'recordings'; document.querySelector('nav').append(tab);
  const panel = node('section'); panel.dataset.panel = 'recordings'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'), list = node('div', '', 'list'), editor = node('div'), details = node('div'), status = node('p', '', 'muted'); panel.append(card);
  card.append(node('h2', 'Record an interview'), node('p', 'Prepare a recording for your enrolled phone or laptop, then confirm and start on that unlocked device. Keep the recording indicator visible. Phone capture is for face-to-face conversations; use a laptop for call audio. Nothing is transcribed automatically.'), status, editor, list, details, button('Refresh recordings', load));
  let generation = 0, detailGeneration = 0, pending = null, paidOperation = null;
  function field(form, title, type = 'text') { const label = node('label', title), input = node('input'); input.type = type; label.append(input); form.append(label); return input; }
  function check(form, title) { const label = node('label', '', 'inline'), input = node('input'); input.type = 'checkbox'; label.append(input, document.createTextNode(title)); form.append(label); return input; }
  function requireVisible() { if (document.hidden) throw Error('Open this page on your unlocked device before managing recordings.'); }
  async function load() {
    requireVisible(); const token = ++generation; detailGeneration++; details.replaceChildren(); paidOperation = null;
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
      if (['Complete', 'Incomplete'].includes(recording.status)) row.append(button('Transcript and speakers', () => showTranscript(recording)));
      row.append(button('Delete recording', async () => {
        requireVisible(); if (!confirm('Delete this recording and its audio? Linked transcript and derived practice will also be removed.')) return;
        const current = generation; await api(`/recordings/${recording.id}/delete`, { revision: recording.revision, locked: false });
        if (current === generation && !document.hidden) { await load(); notice('Recording deleted.'); }
      })); list.append(row);
    }
  }
  async function showTranscript(recording) {
    requireVisible(); const token = ++detailGeneration;
    const progress = await api(`/recordings/${recording.id}/transcription?locked=false`);
    if (token === detailGeneration && !document.hidden) renderTranscript(recording, progress?.quoteId ? progress : null);
  }
  function renderTranscript(recording, progress) {
    details.replaceChildren(node('h3', `Transcript: ${recording.title}`));
    const token = detailGeneration;
    const live = () => token === detailGeneration && !document.hidden;
    details.append(node('p', 'Capture intervals are not word timings. Phone speech starts as unknown; label only segments you can identify. Third-party words stay for your own practice and cannot become memory or agent context.', 'muted'));
    if (!recording.audioDeleted) details.append(button('Get transcription quote', async () => {
      requireVisible(); const quoted = await api(`/recordings/${recording.id}/transcription/quote`, { revision: progress?.recordingRevision ?? recording.revision, locked: false });
      if (live()) { paidOperation = null; renderTranscript(recording, quoted); }
    }));
    if (!progress) { details.append(node('p', 'No transcription requested. Review a quote before sending audio.')); return; }
    details.append(node('p', `${progress.status} · ${progress.model} · Maximum for this quote £${(Math.ceil(Number(progress.maximumGbp) * 100) / 100).toFixed(2)} · ${progress.pieces.filter(p => p.status === 'Pending').length} pieces pending`));
    if (progress.status === 'Quoted') details.append(node('p', `Quote expires ${new Date(progress.quoteExpiresAt).toLocaleString()}.`));
    if (progress.errorCode) details.append(node('p', `Stopped: ${progress.errorCode}. No failed piece is automatically replayed.`, 'muted'));
    const uncertain = progress.pieces.filter(p => p.status === 'Uncertain');
    if (uncertain.length) details.append(node('p', `Verify provider billing and reconcile these reservations in the budget controls before continuing: ${uncertain.map(p => p.reservationId).join(', ')}. Failed pieces stay missing.`));
    if (['Quoted', 'Ready', 'Paused', 'Uncertain'].includes(progress.status)) {
      const consent = check(details, 'I confirm this quote and consent to send its audio to my configured transcription provider for my own practice.');
      let reconfirm = null;
      if (uncertain.length) reconfirm = check(details, 'I completed verified billing reconciliation and want to continue with the remaining pieces.');
      const advance = button(uncertain.length ? 'Continue after reconciliation' : 'Confirm and transcribe', async () => {
        requireVisible(); if (!consent.checked || (reconfirm && !reconfirm.checked)) throw Error('Confirm the quote, cloud consent and any required reconciliation first.');
        advance.disabled = true; let current = progress; let reconciled = Boolean(reconfirm?.checked);
        try {
          // Continue only after successful responses within this explicitly confirmed quote.
          // A failed/unknown response stops here; neither reload nor refresh replays paid work.
          do {
            requireVisible(); if (!live()) return;
            const fingerprint = `${recording.id}:${current.quoteId}:${current.revision}`;
            if (paidOperation?.fingerprint !== fingerprint) paidOperation = { fingerprint, id: crypto.randomUUID() };
            current = await api(`/recordings/${recording.id}/transcription/advance`, { requestId: paidOperation.id, quoteId: current.quoteId,
              jobRevision: current.revision, confirmCloudProcessing: true, confirmQuote: current.status === 'Quoted', continueAfterReconciliation: reconciled, locked: false });
            paidOperation = null; reconciled = false;
            if (live()) details.querySelector('[data-transcription-progress]')?.remove();
            if (live()) { const text = node('p', `${current.pieces.filter(p => p.status === 'Succeeded').length} pieces transcribed…`); text.dataset.transcriptionProgress = ''; details.append(text); }
          } while (current.status === 'Ready' && current.pieces.some(p => p.status === 'Pending'));
          if (live()) renderTranscript(recording, current);
        } catch (error) { if (live()) { notice(`${error.message} Refresh progress before deciding what to do next; no paid request was retried.`, true); } }
        finally { if (live()) advance.disabled = false; }
      }); details.append(advance);
    }
    details.append(button('Refresh transcription progress', () => showTranscript(recording)));
    const transcript = progress.transcript;
    if (!transcript) return;
    details.append(node('h4', transcript.partial ? 'Partial transcript — some audio or results are missing' : 'Transcript'), node('p', transcript.timingSource || progress.timingSource, 'muted'));
    for (const segment of transcript.segments) {
      const row = node('div', '', 'list-item'); row.append(node('strong', `${segment.startSeconds.toFixed(1)}–${segment.endSeconds.toFixed(1)}s · ${segment.speaker}`), node('p', segment.text));
      for (const [speaker, label] of [['user', 'Me'], ['interviewer', 'Interviewer'], ['unknown', 'Unknown']]) row.append(button(label, async () => {
        requireVisible(); await api(`/recordings/${recording.id}/transcription/speakers`, { transcriptRevision: transcript.revision, pieceId: segment.recordingPieceId, speaker, locked: false });
        if (live()) await showTranscript(recording);
      })); details.append(row);
    }
  }
  function clear() { generation++; detailGeneration++; pending = null; paidOperation = null; editor.replaceChildren(); list.replaceChildren(); details.replaceChildren(); status.textContent = ''; }
  document.addEventListener('visibilitychange', () => { if (document.hidden) clear(); });
  return { load, clear };
}
