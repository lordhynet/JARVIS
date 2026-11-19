export function initRecordings({ api, node, button, notice, openPractice }) {
  const tab = node('button', 'Interview recordings'); tab.dataset.tab = 'recordings'; document.querySelector('nav').append(tab);
  const panel = node('section'); panel.dataset.panel = 'recordings'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'), list = node('div', '', 'list'), editor = node('div'), details = node('div'), status = node('p', '', 'muted'); panel.append(card);
  card.append(node('h2', 'Record an interview'), node('p', 'Prepare a recording for your enrolled phone or laptop, then confirm and start on that unlocked device. Keep the recording indicator visible. Phone capture is for face-to-face conversations; use a laptop for call audio. Nothing is transcribed automatically.'), status, editor, list, details, button('Refresh recordings', load));
  let generation = 0, detailGeneration = 0, pending = null, paidOperation = null;
  let loadedRecordings = [];
  const audioUrls = new Set();
  function clearAudio() { for (const audio of details.querySelectorAll('audio')) { audio.pause(); audio.removeAttribute('src'); audio.load(); } for (const url of audioUrls) URL.revokeObjectURL(url); audioUrls.clear(); }
  function field(form, title, type = 'text') { const label = node('label', title), input = node('input'); input.type = type; label.append(input); form.append(label); return input; }
  function check(form, title) { const label = node('label', '', 'inline'), input = node('input'); input.type = 'checkbox'; label.append(input, document.createTextNode(title)); form.append(label); return input; }
  function requireVisible() { if (document.hidden) throw Error('Open this page on your unlocked device before managing recordings.'); }
  async function load() {
    requireVisible(); const token = ++generation; detailGeneration++; clearAudio(); details.replaceChildren(); paidOperation = null;
    const [settings, recordings, devices] = await Promise.all([api('/recordings/status'), api('/recordings?locked=false'), api('/devices')]);
    if (token !== generation || document.hidden) return;
    loadedRecordings = recordings;
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
      if (['Complete', 'Incomplete'].includes(recording.status)) row.append(button('Review and practise', () => showPractice(recording)));
      if (['Complete', 'Incomplete'].includes(recording.status)) row.append(button('Interview just finished', () => showRecap(recording)));
      row.append(button('Delete recording', async () => {
        requireVisible(); if (!confirm('Delete this recording and its audio? Linked transcript and derived practice will also be removed.')) return;
        const current = generation; await api(`/recordings/${recording.id}/delete`, { revision: recording.revision, locked: false });
        if (current === generation && !document.hidden) { await load(); notice('Recording deleted.'); }
      })); list.append(row);
    }
  }
  async function showTranscript(recording) {
    requireVisible(); clearAudio(); const token = ++detailGeneration;
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
  function delivery(value) { return `${value.words} words · ${value.wordsPerMinute == null ? 'Pace unavailable' : value.wordsPerMinute + ' words/min (estimate)'} · ${value.fillers} filler candidates · ${value.seconds == null ? 'Duration unavailable' : value.seconds + 's interval'} · opening ${value.openingScore}/2 · ${value.pauseRatio == null ? 'Pauses unavailable' : Math.round(value.pauseRatio * 100) + '% quiet frames'}`; }
  function startControls(recording, page, mode, live) {
    const consent = check(details, mode === 'practice' ? 'I consent to use these real questions and my answers with my configured normal voice provider for this private practice.' : 'I consent to normal cloud voice processing for my private interview recap.');
    let operation = null;
    details.append(button(mode === 'practice' ? 'Practise this interview' : 'Start voice recap', async () => {
      requireVisible(); if (!consent.checked) throw Error('Confirm normal cloud voice consent first.');
      if (!operation) operation = crypto.randomUUID();
      const result = await api(`/recordings/${recording.id}/practice`, { requestId: operation, documentRevision: mode === 'recap' ? null : page.documentRevision, mode, confirmCloudPractice: true, locked: false });
      if (live()) await openPractice(result.sessionId);
    }));
    details.append(node('p', 'Starting prepares a private conversation only. Normal voice still needs its usual budget and cloud consent. JARVIS uses its own voice. Optional AI debrief is off; no agent or extra paid assessment is started.', 'muted'));
  }
  async function showRecap(recording) {
    requireVisible(); clearAudio(); const token = ++detailGeneration; const live = () => token === detailGeneration && !document.hidden;
    const page = await api(`/recordings/${recording.id}/practice?locked=false`); if (!live()) return;
    details.replaceChildren(node('h3', 'Interview just finished'), node('p', 'Record how it felt, what went well and what happens next. Your Core-captured recap stays attached to this recording.'));
    startControls(recording, null, 'recap', live);
    for (const recap of page.practices.filter(p => p.mode === 'recap' && p.sourceCurrent !== false)) {
      details.append(button(`Open recap · ${recap.status}`, async () => { requireVisible(); if (live()) await openPractice(recap.sessionId); }));
      if (recap.capturedRecap) details.append(node('p', recap.capturedRecap));
    }
  }
  async function showPractice(recording) {
    requireVisible(); clearAudio(); const token = ++detailGeneration; const live = () => token === detailGeneration && !document.hidden;
    const page = await api(`/recordings/${recording.id}/practice?locked=false`); if (!live()) return;
    details.replaceChildren(node('h3', `Practise: ${recording.title}`), node('p', page.partial ? 'Partial interview — some audio or text is missing.' : 'Private interview review'), node('p', page.timingSource, 'muted'));
    if (!page.questions.length) details.append(node('p', 'No labelled interviewer questions yet. Review transcript speakers first. Questions longer than 1,200 characters are omitted.'));
    else startControls(recording, page, 'practice', live);
    for (const question of page.questions) {
      const row = node('div', '', 'list-item'); row.append(node('h4', `${question.startSeconds.toFixed(1)}–${question.endSeconds.toFixed(1)}s · ${question.question}`), node('p', question.alignment, 'muted'), node('p', question.originalAnswer || 'No reviewed Me answer aligned to this interval.'), node('p', delivery(question.delivery)), node('p', question.delivery.timingSource, 'muted'));
      for (const coverage of question.coverage) row.append(node('p', `${coverage.dimension}: ${coverage.score}/${coverage.maximum} text markers — ${coverage.basis}`));
      for (const gap of question.gaps) row.append(node('p', gap, 'muted'));
      for (const story of question.stories) {
        row.append(node('p', `Story: ${story.title} (v${story.version}), ${story.suppliedClaims} current supplied claims shown (up to 3). ${story.basis}`));
        for (const claim of story.claims || []) row.append(node('p', `Supplied evidence: ${claim.quote} · source ${claim.documentId} revision ${claim.documentRevision}`));
        if (!story.suppliedClaims) row.append(node('p', 'Evidence gap: this matched story has no current source-backed claim. Verify the facts before using it.', 'muted'));
      }
      if (!question.stories.length) row.append(node('p', 'No story matched by shared text terms. Choose evidence from your own experience.', 'muted'));
      for (const piece of question.audioPieces) {
        const audio = node('audio'); audio.controls = true; audio.preload = 'none'; audio.setAttribute('controlsList', 'nodownload');
        row.append(button('Load my answer audio', async () => {
          requireVisible(); const blob = await api(`/recordings/${recording.id}/practice/${question.id}/audio/${piece}?revision=${page.documentRevision}&locked=false`, undefined, 'GET', true);
          if (!live()) return; const url = URL.createObjectURL(blob); audioUrls.add(url); audio.src = url;
        }), audio);
      }
      if (question.cardApproved) row.append(node('p', 'Practice card approved.'));
      else if (question.gaps.length) row.append(button('Approve practice card', async () => {
        requireVisible(); await api(`/recordings/${recording.id}/practice/cards`, { documentRevision: page.documentRevision, questionId: question.id, confirm: true, locked: false });
        if (live()) await showPractice(recording);
      }));
      details.append(row);
    }
    for (const practice of page.practices) {
      const row = node('div', '', 'list-item'); row.append(node('h4', `${practice.mode === 'recap' ? 'Recap' : 'Practice'} · ${practice.status}`));
      if (practice.sourceCurrent === false) row.append(node('p', 'Source labels or answer retention changed. Start a fresh practice.', 'muted'));
      else {
        row.append(button(practice.mode === 'recap' ? 'Open recap' : 'Resume practice', async () => { requireVisible(); if (live()) await openPractice(practice.sessionId); }));
        if (practice.capturedRecap) row.append(node('p', practice.capturedRecap));
        for (const answer of practice.answers || []) {
          row.append(node('p', answer.capturedAnswer));
          const table = node('table'), head = node('tr'); head.append(node('th', 'Original answer (interval estimate)'), node('th', 'New captured answer')); table.append(head);
          const metrics = node('tr'); metrics.append(node('td', delivery(answer.comparison.before)), node('td', delivery(answer.comparison.after))); table.append(metrics);
          for (let index = 0; index < answer.comparison.beforeCoverage.length; index++) {
            const before = answer.comparison.beforeCoverage[index], after = answer.comparison.afterCoverage[index], coverage = node('tr');
            coverage.append(node('td', `${before.dimension}: ${before.score}/${before.maximum}`), node('td', `${after.dimension}: ${after.score}/${after.maximum}`)); table.append(coverage);
          }
          row.append(table, node('p', 'English text checklist markers only; this is not a competence grade. Original pauses are unavailable.', 'muted'));
        }
      }
      details.append(row);
    }
    details.append(node('h4', 'Thank-you note — draft only'), node('p', page.thankYouDraft), node('p', 'Review and personalise this draft yourself. Nothing is sent.', 'muted'));
  }
  async function open(id) {
    requireVisible(); document.querySelectorAll('[data-tab]').forEach(t => t.classList.toggle('active', t === tab));
    document.querySelectorAll('[data-panel]').forEach(p => p.hidden = p !== panel);
    await load(); requireVisible(); const recording = loadedRecordings.find(r => r.id === id);
    if (!recording) throw Error('This private interview source is no longer available.');
    await showPractice(recording);
  }
  function clear() { generation++; detailGeneration++; pending = null; paidOperation = null; loadedRecordings = []; clearAudio(); editor.replaceChildren(); list.replaceChildren(); details.replaceChildren(); status.textContent = ''; }
  document.addEventListener('visibilitychange', () => { if (document.hidden) clear(); });
  return { load, clear, open };
}
