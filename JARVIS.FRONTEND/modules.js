// Canonical browser feature modules. Package with scripts/sync-frontend.ps1 in Core.
// All user/provider strings enter the DOM through textContent or value.
export function initModules({ api, notice, node, button, on, selectedSession, ensureSession }) {
  const panels = new Map(); const pending = new Map(); const drafts = new Map(); let contextId = null;
  let exercises = [], stories = [], questions = [], prompt = null, coach = null, drill = null, report = null, programme = null, editStory = null;
  const el = (tag, text, parent, cls) => { const value = node(tag, text, cls); parent?.append(value); return value; };
  const field = (parent, title, tag = 'input', options = {}) => {
    const label = el('label', title, parent); const input = document.createElement(tag); Object.assign(input, options); input.setAttribute('aria-label', title); label.append(input); return input;
  };
  const option = (select, title, value) => { const item = node('option', title); item.value = value; select.append(item); };
  const action = (parent, title, run, cls) => { const value = button(title, run); if (cls) value.className = cls; parent.append(value); return value; };
  function panel(id, title, intro) {
    const nav = document.querySelector('nav'); const tab = node('button', title); tab.dataset.tab = id; nav.append(tab);
    const section = document.createElement('section'); section.dataset.panel = id; section.hidden = true;
    const card = el('div', '', section, 'card'); el('p', 'YOUR SHARED CORE', card, 'eyebrow'); el('h2', title, card); el('p', intro, card, 'muted');
    document.getElementById('workspace').append(section); panels.set(id, card); return card;
  }
  const coaching = panel('coaching', 'Coaching', 'Communication and interview practice, anchored to your real stories. Text observations are heuristics, not AI grades.');
  const coachingForm = el('form', '', coaching);
  const exerciseInput = field(coachingForm, 'Exercise', 'select');
  const storyInput = field(coachingForm, 'Source story', 'select'); const claimInput = field(coachingForm, 'Saved CV claim', 'select');
  const openPrompt = el('button', 'Open coaching prompt', coachingForm, 'primary');
  const promptBox = el('div', '', coaching, 'list-item'); promptBox.hidden = true;
  const answerForm = el('form', '', coaching); answerForm.hidden = true;
  const answer = field(answerForm, 'Your coaching answer', 'textarea', { required: true, maxLength: 20000, rows: 6 });
  const seconds = field(answerForm, 'Reported elapsed seconds · optional', 'input', { type: 'number', min: '1', max: '3600', step: 'any' });
  el('small', 'Leave time blank for typed answers. Reported timing includes pauses and is not an acoustic measurement.', answerForm);
  const saveAnswer = el('button', 'Save & review text', answerForm, 'primary');
  const feedback = el('div', '', coaching, 'list'); const history = el('div', '', coaching, 'list');
  const reportBox = el('div', '', coaching, 'list');
  const deliveryBox = el('div', '', coaching, 'card');
  const scenarioBox = el('div', '', coaching, 'card');
  async function loadScenarios() {
    const entries = await api('/coaching/scenarios'); scenarioBox.replaceChildren(); el('h3', 'Coaching scenarios', scenarioBox);
    el('p', 'Use Everyday voice to start a panel interview, recruiter screen, presentation or hostile Q&A. JARVIS asks one authored question and waits for your answer. Phone and browser use the same audio flow.', scenarioBox);
    for (const entry of entries) { const scenario = entry.scenario; const row = el('details', '', scenarioBox, 'list-item');
      el('summary', scenario.title + (entry.active ? ' · Everyday voice' : ' · Disabled or unavailable'), row);
      el('p', 'Personas: ' + scenario.personas.map(p => p.name + ' (' + p.style + ')').join('; '), row);
      el('p', scenario.turns.length + ' questions · ' + scenario.rubric.map(r => r.dimension).join(', '), row);
      el('p', 'Say “Start ' + scenario.title + '”. No independent competence grade is implied by checklist coverage.', row);
    }
    const session = selectedSession(); if (!session) return;
    for (const debrief of await api('/coaching/sessions/' + session.id + '/scenario-debriefs')) {
      const row = el('details', '', scenarioBox, 'list-item'); el('summary', debrief.title + ' · ' + debrief.status + ' · ' + debrief.answers + ' answers', row);
      el('p', debrief.assessmentKind, row);
      debrief.scores.forEach(s => el('p', s.dimension + ': ' + s.score + '/' + s.maximum + ' · ' + s.basis, row));
      debrief.followUps.forEach(f => el('p', 'Next: ' + f, row));
      debrief.evidence.forEach(e => { const item = el('small', 'Saved answer ' + e.attemptId + ' · revision ' + e.revision + (e.voiceTurnId ? ' · captured speech linked' : ' · speech timing unavailable'), row); item.style.overflowWrap = 'anywhere'; });
      for (const delivery of debrief.delivery) el('p', 'Answer pace: ' + (delivery.wordsPerMinute == null ? 'unavailable' : delivery.wordsPerMinute + ' words/min') + ' · fillers ' + delivery.fillers + ' · I ' + delivery.firstPersonSingular + ' / we ' + delivery.firstPersonPlural, row);
    }
  }
  async function loadDelivery() {
    const sessions = await api('/coaching/delivery'); deliveryBox.replaceChildren(); el('h3', 'Speech delivery', deliveryBox);
    el('p', 'Unrestricted user speech only. Pace uses captured duration. Fillers and the point-first opening score (0–2) are English text heuristics. Pause ratio uses quiet intervals within captured speech, not time between speakers.', deliveryBox, 'muted');
    if (!sessions.length) el('p', 'No eligible voice turns yet. Timing is never inferred for typed answers.', deliveryBox);
    for (const session of sessions) {
      const entry = el('details', '', deliveryBox, 'list-item');
      el('summary', 'Session ' + session.sessionId.slice(0, 8) + ' · ' + session.turns + ' turns · ' + session.words + ' words', entry);
      el('p', 'Pace: ' + (session.wordsPerMinute == null ? 'unavailable' : session.wordsPerMinute + ' words/min') + ' · Filler candidates: ' + session.fillers + ' · I: ' + session.firstPersonSingular + ' · We: ' + session.firstPersonPlural, entry);
      el('p', 'Pause ratio: ' + (session.pauseRatio == null ? 'unavailable' : Math.round(session.pauseRatio * 100) + '% (quiet-audio heuristic)') + ' · Opening score: ' + session.openingScore + '/2', entry);
      const trend = el('ol', '', entry);
      for (const turn of session.trend) el('li', new Date(turn.at).toLocaleTimeString() + ' · ' + turn.words + ' words · ' + (turn.wordsPerMinute == null ? 'pace unavailable' : turn.wordsPerMinute + ' words/min') + ' · fillers ' + turn.fillers + ' · opening ' + turn.openingScore + '/2', trend);
    }
  }
  action(coaching, 'Review current session', async () => {
    const session = selectedSession(); if (!session) throw Error('Open a conversation or coaching prompt first.');
    report = await api(`/sessions/${session.id}/review`); reportBox.replaceChildren();
    el('h3', report.title, reportBox); el('p', `${report.technicalAttempts} technical answers · ${report.coachingAttempts} coaching answers · ${report.skippedQuestions} skipped`, reportBox);
    el('small', report.assessmentSource, reportBox); report.textObservations.forEach(x => el('p', x.detail, reportBox)); report.nextDrills.forEach(x => el('p', `Try next: ${x}`, reportBox));
    action(reportBox, 'Download this session review', () => {
      const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }); const url = URL.createObjectURL(blob);
      const link = document.createElement('a'); link.href = url; link.download = `jarvis-session-${report.sessionId}.json`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
  });
  function request(kind, body) {
    const key = kind + JSON.stringify(body); if (!pending.has(key)) { if (pending.size >= 256) pending.delete(pending.keys().next().value); pending.set(key, crypto.randomUUID()); }
    return { requestId: pending.get(key), ...body };
  }
  async function mutate(kind, path, body, method) {
    const result = await api(path, request(kind, body), method);
    pending.delete(kind + JSON.stringify(body)); return result;
  }
  function aiFeedback(parent, kind, id, revision, textRevision, accept) {
    const consent = field(parent, 'Allow this saved answer to be sent for paid AI feedback', 'input', { type: 'checkbox' });
    const result = action(parent, 'Request AI feedback', async () => {
      if (!consent.checked) throw Error('Choose cloud-processing consent before requesting AI feedback.');
      result.disabled = true;
      try {
        const status = await api('/coaching/ai-status');
        if (!status.ready) throw Error(status.detail);
        const value = await mutate(`ai-${kind}-${id}`, `/${kind}/attempts/${id}/ai-assessments`, {
          expectedRevision: revision, textRevision, allowCloudProcessing: true,
          timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/London'
        });
        accept(value); notice('AI feedback saved. Scores remain model assessments, with answer evidence.');
      } finally { result.disabled = !consent.checked; }
    });
    result.disabled = true; consent.addEventListener('change', () => { result.disabled = !consent.checked; });
  }
  function sourceSelection() {
    const exercise = exercises.find(x => x.id === exerciseInput.value); if (!exercise) return;
    const old = storyInput.value; storyInput.replaceChildren(); option(storyInput, 'Choose a saved story', '');
    stories.forEach(x => option(storyInput, x.content.title, x.id)); storyInput.value = old;
    storyInput.parentElement.hidden = !exercise.requiresStory; claimInput.parentElement.hidden = !exercise.requiresClaim;
    claims();
  }
  function claims() { claimInput.replaceChildren(); option(claimInput, 'Choose a saved CV claim', ''); (stories.find(x => x.id === storyInput.value)?.content.cvClaims || []).forEach((claim, i) => option(claimInput, claim, String(i))); }
  exerciseInput.addEventListener('change', sourceSelection); storyInput.addEventListener('change', claims);
  coachingForm.addEventListener('submit', async event => {
    event.preventDefault(); openPrompt.disabled = true;
    try {
      const exercise = exercises.find(x => x.id === exerciseInput.value); const story = exercise.requiresStory ? stories.find(x => x.id === storyInput.value) : null;
      if (exercise.requiresStory && !story) throw Error('Save and select a real Story Bank entry first.');
      if (exercise.requiresClaim && claimInput.value === '') throw Error('Select an existing CV claim.');
      const query = new URLSearchParams({ exerciseId: exercise.id }); if (story) { query.set('storyId', story.id); query.set('storyVersion', story.version); }
      if (exercise.requiresClaim) query.set('claimIndex', claimInput.value);
      const loaded = await api(`/coaching/prompt?${query}`);
      const session = await ensureSession(exercise.area === 'Communication' ? 'Communication' : 'MockInterview', exercise.title);
      await api(`/sessions/${session.id}/checkpoints`, { summary: `Coaching: ${exercise.title}`, currentTask: `coaching:${exercise.id}` });
      contextId = session.id; prompt = { ...loaded, sessionId: session.id }; coach = null; answer.value = ''; seconds.value = ''; feedback.replaceChildren(); renderPrompt(); await loadHistory();
    } catch (error) { notice(error.message, true); } finally { openPrompt.disabled = false; }
  });
  function renderPrompt() {
    promptBox.replaceChildren(); promptBox.hidden = !prompt && !coach; answerForm.hidden = promptBox.hidden;
    const exercise = coach?.exercise || prompt?.exercise; if (!exercise) return;
    el('h3', exercise.title, promptBox); el('p', prompt?.question || exercise.prompt, promptBox); el('p', exercise.framework.join(' → '), promptBox);
    if (exercise.targetSeconds) el('small', `Practice target: ${exercise.targetSeconds}s. Not a measured duration.`, promptBox);
    saveAnswer.textContent = coach ? 'Save correction' : 'Save & review text'; saveAnswer.disabled = coach?.sourceCurrent === false;
  }
  answerForm.addEventListener('submit', async event => {
    event.preventDefault(); saveAnswer.disabled = true;
    try {
      const reportedDurationSeconds = seconds.value === '' ? null : Number(seconds.value);
      if (coach) {
        coach = await mutate(`coach-correct-${coach.attempt.id}`, `/coaching/attempts/${coach.attempt.id}`, { expectedRevision: coach.attempt.revision, text: answer.value, reportedDurationSeconds }, 'PATCH');
      } else {
        if (!prompt) throw Error('Choose a prompt first.');
        coach = await mutate('coach-save', '/coaching/attempts', { sessionId: prompt.sessionId, exerciseId: prompt.exercise.id, exerciseVersion: prompt.exercise.version, text: answer.value,
          storyId: prompt.storyId, storyVersion: prompt.storyVersion, claimIndex: prompt.claimIndex, reportedDurationSeconds });
      }
      renderFeedback(); renderPrompt(); await loadHistory(); notice('Saved to Core. Text observations are not a competence grade.');
    } catch (error) { notice(error.message, true); } finally { saveAnswer.disabled = coach?.sourceCurrent === false; }
  });
  function renderFeedback() {
    feedback.replaceChildren(); if (!coach) return; const view = coach; const analysis = view.analysis;
    el('h3', 'Text observations', feedback); el('p', `${analysis.wordCount} words · ${analysis.fillerCandidates} possible filler phrases`, feedback);
    el('p', analysis.approximateWordsPerMinute === null ? 'Pace, pauses and confidence are unavailable from text alone.' : `Approximate ${analysis.approximateWordsPerMinute} words/minute using user-reported elapsed time.`, feedback);
    if (!view.sourceCurrent) el('p', 'Source story changed. Open a fresh prompt before saving another answer.', feedback);
    analysis.observations.forEach(x => { el('p', x.detail, feedback); if (x.evidence.length) el('small', `Text evidence: ${x.evidence.join(' · ')}`, feedback); });
    analysis.nextDrills.forEach(x => el('p', `Try next: ${x}`, feedback)); view.exercise.followUps.forEach(x => el('p', `Follow-up: ${x}`, feedback));
    if (view.aiAssessment) {
      el('h3', `AI feedback · ${view.aiAssessment.model}`, feedback); el('p', view.aiAssessment.summary, feedback);
      view.aiAssessment.scores.forEach(x => { el('p', `${x.dimension}: ${x.value ?? 'unrated'} · ${x.rationale}`, feedback); if (x.evidence) el('blockquote', x.evidence, feedback); });
      view.aiAssessment.followUps.forEach(x => el('p', `AI follow-up: ${x}`, feedback));
    }
    if (view.sourceCurrent) aiFeedback(feedback, 'coaching', view.attempt.id, view.attempt.revision, view.attempt.answers.at(-1).version,
      value => { coach = value; renderFeedback(); renderPrompt(); });
    const versions = el('details', '', feedback); el('summary', 'Saved answer versions', versions); view.attempt.answers.forEach(x => el('p', `Version ${x.version}: ${x.text}`, versions));
    action(feedback, 'Delete coaching answer', async () => {
      if (!confirm('Delete this saved answer and all its versions?')) return;
      await api(`/coaching/attempts/${view.attempt.id}?expectedRevision=${view.attempt.revision}`, undefined, 'DELETE');
      coach = null; answer.value = ''; seconds.value = ''; renderFeedback(); renderPrompt(); reportBox.replaceChildren(); await loadHistory();
    }, 'danger');
    if (prompt) action(feedback, 'Start another answer', () => { coach = null; answer.value = ''; seconds.value = ''; renderFeedback(); renderPrompt(); });
  }
  async function loadHistory() {
    history.replaceChildren(); const session = selectedSession(); if (!session) return;
    const attempts = await api(`/coaching/sessions/${session.id}/attempts`);
    if (attempts.length) el('h3', 'Saved in this session', history);
    attempts.forEach(x => action(history, `${x.title} · ${x.wordCount} words`, async () => {
      coach = await api(`/coaching/attempts/${x.id}`); contextId = coach.attempt.sessionId; prompt = null; const current = coach.attempt.answers.at(-1); answer.value = current.text; seconds.value = current.reportedDurationSeconds ?? ''; renderPrompt(); renderFeedback();
    }));
  }

  const storyPanel = panel('stories', 'Story Bank', 'Keep real facts, decisions and outcomes. Blank metrics are better than invented ones.');
  const storyForm = el('form', '', storyPanel); const storyFields = new Map();
  const titles = { title: 'Title', companyProject: 'Company / project', serviceFeature: 'Exact service / feature', context: 'Context', problem: 'Problem', constraints: 'Constraints', personalOwnership: 'Personal ownership', technicalDecisions: 'Technical decisions', tradeOffs: 'Trade-offs', implementation: 'Implementation', collaboration: 'Collaboration', failureModes: 'Failure modes', result: 'Outcome', metrics: 'Verified metrics', changeToday: 'What I would change today', followUpQuestions: 'Follow-up questions · one per line', cvClaims: 'CV claims · one per line' };
  for (const [key, title] of Object.entries(titles)) storyFields.set(key, field(storyForm, title, ['title', 'companyProject', 'serviceFeature'].includes(key) ? 'input' : 'textarea', { required: ['title', 'companyProject', 'serviceFeature'].includes(key), maxLength: key === 'title' ? 200 : ['companyProject', 'serviceFeature'].includes(key) ? 300 : ['followUpQuestions', 'cvClaims'].includes(key) ? 60000 : 8000 }));
  const storySave = el('button', 'Save real story', storyForm, 'primary');
  action(storyPanel, 'New story / clear editor', () => { editStory = null; storyForm.reset(); storySave.textContent = 'Save real story'; });
  const storyList = el('div', '', storyPanel, 'list');
  storyForm.addEventListener('submit', async event => {
    event.preventDefault(); storySave.disabled = true;
    try {
      const content = Object.fromEntries([...storyFields].map(([key, input]) => [key, ['followUpQuestions', 'cvClaims'].includes(key) ? input.value.split('\n').map(x => x.trim()).filter(Boolean) : input.value]));
      const saved = editStory ? await api(`/stories/${editStory.id}`, { expectedVersion: editStory.version, content }, 'PUT') : await mutate('story-create', '/stories', { content });
      editStory = saved; storySave.textContent = `Save changes · v${saved.version}`; await loadStories(); notice('Real story saved.');
    } catch (error) { notice(error.message, true); } finally { storySave.disabled = false; }
  });
  async function loadStories() {
    stories = await api('/stories'); storyList.replaceChildren();
    stories.forEach(story => {
      const card = el('div', '', storyList, 'list-item'); el('h3', story.content.title, card); el('small', `${story.content.companyProject} · version ${story.version}`, card);
      action(card, 'Edit story', () => { editStory = story; for (const [key, input] of storyFields) input.value = Array.isArray(story.content[key]) ? story.content[key].join('\n') : story.content[key] || ''; storySave.textContent = `Save changes · v${story.version}`; storyForm.scrollIntoView({ block: 'start', behavior: 'smooth' }); });
      action(card, 'Delete story', async () => {
        if (!confirm('Delete this story and its linked coaching answers?')) return;
        await api(`/stories/${story.id}?expectedVersion=${story.version}`, undefined, 'DELETE');
        for (const [id, draft] of drafts) if (draft.coach?.attempt.storyId === story.id || draft.prompt?.storyId === story.id) drafts.delete(id);
        if (editStory?.id === story.id) { editStory = null; storyForm.reset(); storySave.textContent = 'Save real story'; }
        if (coach?.attempt.storyId === story.id || prompt?.storyId === story.id) { coach = null; prompt = null; answer.value = ''; seconds.value = ''; renderPrompt(); renderFeedback(); history.replaceChildren(); reportBox.replaceChildren(); }
        await loadStories();
      }, 'danger');
    });
    if (!stories.length) el('p', 'Add your first real story. No fabricated examples are seeded.', storyList);
  }

  const training = panel('training', 'Technical practice', 'Answer before revealing the reference. Self ratings and optional AI feedback share Core’s review schedule, with their sources recorded.');
  let guided = null, guideEpoch = 0; const guideDrafts = new Map();
  const guideCard = el('div', '', training, 'list-item'); el('h3', 'Guided drills · nine modes', guideCard);
  const guideMode = field(guideCard, 'Guided drill mode', 'select'); const guideDescription = el('p', '', guideCard, 'muted');
  const guideBody = el('div', '', guideCard, 'list'); const guideHistory = el('div', '', guideCard, 'list'); let modeCatalog = [];
  guideMode.addEventListener('change', () => { guideDescription.textContent = modeCatalog.find(x => x.mode === guideMode.value)?.description || ''; });
  action(guideCard, 'Start guided drill', async () => {
    const session = await ensureSession('General', 'Guided technical practice'); const epoch = guideEpoch;
    const result = await mutate('start-guided', '/training/drills', { sessionId: session.id, mode: guideMode.value });
    if (epoch !== guideEpoch) return; guided = result; renderGuided(); await loadGuidedHistory();
  }, 'primary');
  action(guideCard, 'Load saved guided drills', loadGuidedHistory);
  async function loadGuidedHistory() {
    const session = selectedSession(); if (!session) return; const epoch = guideEpoch;
    const saved = await api(`/training/sessions/${session.id}/drills`); if (epoch !== guideEpoch) return;
    guideHistory.replaceChildren();
    saved.forEach(view => action(guideHistory, `${view.drill.mode} · ${view.drill.status} · ${view.drill.attemptIds.length}/${view.drill.steps.length} stages`, async () => {
      const token = guideEpoch; const value = await api(`/training/drills/${view.drill.id}`);
      if (token !== guideEpoch) return; guided = value; renderGuided();
    }));
  }
  function renderGuided() {
    guideBody.replaceChildren(); if (!guided) return; const plan = guided.drill;
    el('h4', `${plan.mode} · ${plan.status}`, guideBody); el('p', `${plan.attemptIds.length}/${plan.steps.length} stages answered or skipped. Completion is not a mastery grade.`, guideBody);
    if (plan.status === 'Active') {
      const step = plan.steps[plan.attemptIds.length]; const draftKey = `${plan.id}:${step.index}`;
      el('h4', `Stage ${step.index + 1}: ${step.title}`, guideBody); el('p', step.prompt, guideBody);
      if (step.suggestedSeconds) el('small', `Target: 20–${step.suggestedSeconds}s. Text does not measure duration.`, guideBody);
      if (step.codeSnippet) el('pre', step.codeSnippet, guideBody);
      const form = el('form', '', guideBody); const text = field(form, 'Guided answer', 'textarea', { required: true, rows: 5, maxLength: 20000, value: guideDrafts.get(draftKey) || '' });
      text.addEventListener('input', () => guideDrafts.set(draftKey, text.value)); el('button', 'Save guided stage', form, 'primary');
      async function save(skipped) {
        const epoch = guideEpoch;
        const submittedText = skipped ? '' : text.value;
        const result = await mutate(`guided-${plan.id}-${step.index}`, '/training/attempts', { sessionId: plan.sessionId, questionId: step.questionId,
          questionVersion: step.questionVersion, mode: plan.mode, text: submittedText, skipped, drillId: plan.id, stepIndex: step.index });
        if (guideDrafts.get(draftKey) === submittedText) guideDrafts.delete(draftKey);
        const current = await api(`/training/drills/${plan.id}`); if (epoch !== guideEpoch || guided?.drill.id !== plan.id) return;
        guided = current; drill = result; drillAnswer.value = result.currentAnswer; renderGuided(); if (!skipped) renderDrill(); await loadGuidedHistory();
      }
      form.addEventListener('submit', async event => { event.preventDefault(); event.submitter.disabled = true; try { await save(false); } catch (error) { notice(error.message, true); } finally { event.submitter.disabled = false; } });
      action(guideBody, 'Skip guided stage without a score', () => save(true));
      action(guideBody, 'Reload guided progress', async () => { const epoch = guideEpoch; const current = await api(`/training/drills/${plan.id}`); if (epoch !== guideEpoch) return; guided = current; renderGuided(); });
      action(guideBody, 'Cancel guided drill', async () => { const epoch = guideEpoch; const value = await api(`/training/drills/${plan.id}/cancel`, { expectedRevision: plan.revision }); if (epoch !== guideEpoch) return; guided = value; renderGuided(); await loadGuidedHistory(); });
    }
    guided.answers.filter(a => !a.skipped).forEach(attempt => {
      const details = el('details', '', guideBody); el('summary', `Saved stage ${(attempt.stepIndex ?? 0) + 1} · ${attempt.canonicalAssessment?.source || 'unrated'}`, details);
      el('p', attempt.currentAnswer, details); const step = plan.steps[attempt.stepIndex]; el('p', `Reference: ${step.reference}`, details);
      action(details, 'Review this guided answer', () => { drill = attempt; drillAnswer.value = attempt.currentAnswer; renderDrill(); });
    });
    for (const [key, text] of guideDrafts) {
      if (!key.startsWith(`${plan.id}:`) || plan.status === 'Active' && key === `${plan.id}:${plan.attemptIds.length}` || !text.trim()) continue;
      const index = Number(key.split(':').at(-1)); if (guided.answers.some(a => a.stepIndex === index && a.currentAnswer === text.trim())) continue;
      const details = el('details', '', guideBody); el('summary', `Preserved local draft · stage ${index + 1}`, details); el('p', text, details);
    }
  }
  const questionInput = field(training, 'Technical question', 'select'); const drillMode = field(training, 'Practice mode', 'select');
  for (const value of ['RapidDrill', 'ExplainSimply', 'SpacedReview']) option(drillMode, value, value);
  const questionBox = el('div', '', training, 'list-item');
  const drillForm = el('form', '', training); const drillAnswer = field(drillForm, 'Your technical answer', 'textarea', { required: true, maxLength: 20000, rows: 5 });
  el('button', 'Save technical answer', drillForm, 'primary'); const drillResult = el('div', '', training, 'list');
  questionInput.addEventListener('change', () => { drill = null; questionBox.textContent = questions.find(x => x.id === questionInput.value)?.prompt || ''; drillAnswer.value = ''; drillResult.replaceChildren(); });
  drillForm.addEventListener('submit', async event => {
    event.preventDefault(); const submit = event.submitter; submit.disabled = true;
    const text = drillAnswer.value; const mode = drillMode.value;
    questionInput.disabled = drillMode.disabled = drillAnswer.disabled = true;
    try {
      const question = questions.find(x => x.id === questionInput.value); const session = await ensureSession('General', 'Technical practice');
      contextId = session.id;
      await api(`/sessions/${session.id}/checkpoints`, { summary: `Practising: ${question.title}`, currentTask: `training-question:${question.id}` });
      drill = await mutate('technical-answer', '/training/attempts', { sessionId: session.id, questionId: question.id, questionVersion: question.version, mode, text });
      drillAnswer.value = text;
      renderDrill(); notice('Technical answer saved.');
    } catch (error) { notice(error.message, true); } finally { submit.disabled = false; questionInput.disabled = drillMode.disabled = drillAnswer.disabled = false; }
  });
  function renderDrill() {
    drillResult.replaceChildren(); if (!drill) return; const question = questions.find(x => x.id === drill.questionId);
    const reference = el('details', '', drillResult); el('summary', 'Compare with reference', reference); el('p', question.reference, reference); question.followUps.forEach(x => el('p', x, reference));
    const source = el('a', 'Primary reference', reference); source.href = question.sourceUrl; source.target = '_blank'; source.rel = 'noopener noreferrer';
    el('h3', 'Self-assessment · technical accuracy', drillResult); el('small', '0 missed · 1 limited · 2 partial · 3 strong · 4 complete. Strong ratings need an exact answer excerpt.', drillResult);
    const form = el('form', '', drillResult); const score = field(form, 'Self rating', 'select'); option(score, 'Unrated', ''); for (let i = 0; i <= 4; i++) option(score, String(i), String(i));
    const evidence = field(form, 'Exact excerpt from saved answer', 'textarea', { maxLength: 512 }); const rationale = field(form, 'Why this rating?', 'textarea', { maxLength: 512 });
    el('button', 'Save self-assessment', form, 'primary');
    form.addEventListener('submit', async event => {
      event.preventDefault(); const submit = event.submitter; submit.disabled = true;
      try {
        drill = await mutate(`technical-assessment-${drill.id}`, `/training/attempts/${drill.id}/assessments`, { expectedRevision: drill.revision, textRevision: drill.textRevision, scores: [{ dimension: 'TechnicalAccuracy', value: score.value === '' ? null : Number(score.value), evidence: evidence.value, rationale: rationale.value }], timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/London' });
        renderDrill(); notice('Self-assessment saved; Core recalculated the review schedule.');
      } catch (error) { notice(error.message, true); } finally { submit.disabled = false; }
    });
    if (drill.canonicalAssessment) {
      const assessment = drill.canonicalAssessment;
      el('p', `Current ${assessment.source === 'self' ? 'self' : assessment.source} score: ${assessment.contentScore ?? 'unrated'} / 4`, drillResult);
      if (assessment.source.startsWith('ai:')) assessment.scores.forEach(x => { el('p', `${x.dimension}: ${x.value ?? 'unrated'} · ${x.rationale}`, drillResult); if (x.evidence) el('blockquote', x.evidence, drillResult); });
    }
    aiFeedback(drillResult, 'training', drill.id, drill.revision, drill.textRevision, value => { drill = value; renderDrill(); });
    action(drillResult, 'Save corrected answer from editor', async () => { drill = await mutate(`technical-correction-${drill.id}`, `/training/attempts/${drill.id}/answer`, { expectedRevision: drill.revision, text: drillAnswer.value }, 'PATCH'); renderDrill(); });
    action(drillResult, 'Delete technical attempt', async () => { if (!confirm('Delete this answer and assessments?')) return; await api(`/training/attempts/${drill.id}?expectedRevision=${drill.revision}`, undefined, 'DELETE'); drill = null; drillAnswer.value = ''; drillResult.replaceChildren(); }, 'danger');
  }

  const programmePanel = panel('programme', '12-week programme', 'A calendar and explicit self check-ins. Check-ins do not prove practice or competence.');
  const programmeBody = el('div', '', programmePanel);
  async function loadProgramme() {
    const view = await api('/programme'); programme = view.programme; programmeBody.replaceChildren();
    if (!programme) {
      const form = el('form', '', programmeBody); const date = field(form, 'Start date', 'input', { type: 'date', required: true }); const now = new Date(); date.value = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
      el('button', 'Start programme', form, 'primary'); form.addEventListener('submit', async event => { event.preventDefault(); const submit = event.submitter; submit.disabled = true; try { await mutate('programme-start', '/programme', { localStartDate: date.value, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/London' }); await loadProgramme(); } catch (error) { notice(error.message, true); } finally { submit.disabled = false; } });
    } else {
      el('h3', view.currentWeek === 0 ? `Starts ${programme.localStartDate}` : view.currentWeek > 12 ? 'Twelve-week calendar completed' : `Week ${view.currentWeek} of 12`, programmeBody);
      el('p', `${programme.checkIns.length} weekly self check-ins · ${programme.timeZone}`, programmeBody);
      for (const week of view.weeks) {
        const details = el('details', '', programmeBody); details.open = week.number === view.currentWeek;
        el('summary', `Week ${week.number}: ${week.focus}`, details); week.tasks.forEach(x => el('p', x, details)); const checked = programme.checkIns.find(x => x.week === week.number);
        if (checked) el('p', `Your check-in: ${checked.note || 'Checked in'}`, details);
        else if (week.number <= view.currentWeek) {
          const note = field(details, 'Weekly reflection · optional', 'textarea', { maxLength: 1000 });
          action(details, 'Save weekly check-in', async () => { await mutate(`programme-check-${programme.id}`, `/programme/${programme.id}/check-ins`, { expectedRevision: programme.revision, week: week.number, note: note.value }); await loadProgramme(); });
        }
      }
      action(programmeBody, 'Delete programme & reflections', async () => { if (!confirm('Delete this programme and its reflection notes? Separate practice answers remain.')) return; await api(`/programme/${programme.id}?expectedRevision=${programme.revision}`, undefined, 'DELETE'); await loadProgramme(); }, 'danger');
    }
    el('small', view.progressSource, programmeBody);
  }

  const workflows = panel('workflows', 'AI workflows', 'Provider keys stay on Core. Paid drafts require explicit per-run consent and server budget readiness.');
  const workBody = el('div', '', workflows, 'list');
  action(workflows, 'Refresh workflows', loadWorkflows);
  async function loadWorkflows() {
    const [paid, runs, budget, voice] = await Promise.all([
      api('/workflows/paid-ai'), api('/workflows'), api('/budget/status').catch(() => null),
      api('/voice/modes').catch(() => null)
    ]); workBody.replaceChildren();
    el('p', paid.detail, workBody); el('p', `Today committed: $${paid.todayCommittedUsd} / $${paid.dailyLimitUsd}`, workBody);
    if (voice || budget?.voiceUsage?.length) {
      const money = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' });
      const card = el('div', '', workBody, 'list-item');
      el('h3', 'Live voice budget', card);
      for (const mode of voice?.modes ?? []) {
        const cap = mode.dailyCapGbp == null ? '' : ` · ${money.format(mode.dailyCapGbp)} daily cap`;
        const description = mode.pricingReady
          ? `${mode.model} · up to ${money.format(mode.maximumGbpForTenMinutes)} reserved for ten minutes`
          : mode.detail;
        el('p', `${mode.mode} · ${description}${cap}`, card);
      }
      for (const entry of budget?.voiceUsage ?? []) {
        const amount = entry.accountedGbp == null
          ? `${money.format(entry.reservedGbp)} reserved` : `${money.format(entry.accountedGbp)} accounted`;
        el('p', `${entry.mode} · ${entry.model} · ${amount}`, card);
      }
    }
    const exchange = el('div', '', workBody, 'list-item');
    el('h3', 'Budget exchange rate', exchange);
    if (budget) {
      const refresh = budget.conversionBasis?.fxStatus;
      const quote = budget.conversionBasis?.fx ?? refresh?.lastAcceptedQuote;
      el('p', budget.detail, exchange);
      if (quote) {
        const hours = Math.max(0, Math.floor((Date.now() - Date.parse(quote.observedAt)) / 3600000));
        el('p', `${quote.source} · observed ${hours} h ago`, exchange);
      }
      if (refresh?.lastError) el('p', `Last automatic refresh failed. ${refresh.lastError}`, exchange);
      if (refresh?.manualQuoteIgnored) el('p', 'Core ignored the malformed manual exchange rate.', exchange);
    } else el('p', 'Budget exchange rate is unavailable.', exchange);
    action(workBody, 'Build unpaid practice plan', async () => { const body = { notBefore: null }; await api('/workflows/practice-plans', request('practice-plan', body)); notice('Plan accepted. Refresh to check its status.'); await loadWorkflows(); pending.delete('practice-plan' + JSON.stringify(body)); });
    const consent = field(workBody, 'I allow Claude to process selected saved story facts and due-card prompts for this brief.', 'input', { type: 'checkbox' });
    const generate = action(workBody, 'Generate paid interview brief', async () => { if (!consent.checked) throw Error('Approve cloud processing for this brief first.'); const body = { allowCloudProcessing: true }; await api('/workflows/interview-briefs', request('paid-brief', body)); notice('Brief accepted. Refresh to check its status before creating another.'); await loadWorkflows(); pending.delete('paid-brief' + JSON.stringify(body)); }); generate.disabled = !paid.ready;
    runs.forEach(run => {
      const card = el('div', '', workBody, 'list-item'); el('h3', `${run.kind} · ${run.status}`, card);
      if (run.errorCode) el('p', run.errorCode, card);
      if (run.brief) { el('small', 'AI-generated draft · verify facts', card); el('p', run.brief.summary, card); run.brief.drills.forEach(x => el('p', x, card)); }
      if (run.result) { run.result.gaps.forEach(x => el('p', x, card)); run.result.steps.forEach(x => el('p', x.prompt, card)); }
      if (['Queued', 'Running', 'RetryScheduled'].includes(run.status)) action(card, 'Cancel workflow', async () => { await api(`/workflows/${run.id}/cancel`, {}); await loadWorkflows(); });
      action(card, 'Delete workflow result', async () => { if (!confirm('Delete saved result? Minimal spending/retry records remain.')) return; await api(`/workflows/${run.id}`, undefined, 'DELETE'); await loadWorkflows(); }, 'danger');
    });
  }

  const modules = panel('modules', 'All modules', 'A transparent view of implemented capabilities and remaining requirements. Planned modules are not presented as finished.');
  const moduleList = el('div', '', modules, 'grid module-grid');
  async function loadModules() {
    moduleList.replaceChildren(); for (const module of await api('/modules')) {
      const details = el('details', '', moduleList, 'list-item'); el('summary', `${module.title} · ${module.status}`, details);
      module.available.forEach(x => el('p', `Available: ${x}`, details)); module.remaining.forEach(x => el('p', `Pending: ${x}`, details));
    }
  }
  return {
    sessionChanged(id) {
      if (contextId === id) return;
      if (contextId) { if (drafts.size >= 20) drafts.delete(drafts.keys().next().value); drafts.set(contextId, { prompt, coach, answer: answer.value, seconds: seconds.value, drill, drillAnswer: drillAnswer.value }); }
      contextId = id; const saved = drafts.get(id); prompt = saved?.prompt || null; coach = saved?.coach || null; drill = saved?.drill || null;
      guideEpoch++; guided = null; guideBody.replaceChildren(); guideHistory.replaceChildren();
      answer.value = saved?.answer || ''; seconds.value = saved?.seconds || ''; drillAnswer.value = saved?.drillAnswer || '';
      renderPrompt(); renderFeedback(); if (!drill || questions.some(x => x.id === drill.questionId)) renderDrill();
      history.replaceChildren(); reportBox.replaceChildren(); scenarioBox.replaceChildren(); report = null;
    },
    async load(tab) {
      if (tab === 'coaching') { [exercises, stories] = await Promise.all([api('/coaching/exercises'), api('/stories')]); const old = exerciseInput.value; exerciseInput.replaceChildren(); exercises.forEach(x => option(exerciseInput, `${x.area} · ${x.title}`, x.id)); if (exercises.some(x => x.id === old)) exerciseInput.value = old; sourceSelection(); await loadHistory(); await loadDelivery(); await loadScenarios(); }
      if (tab === 'stories') await loadStories();
      if (tab === 'training') { questions = await api('/training/questions'); const old = questionInput.value; questionInput.replaceChildren(); questions.forEach(x => option(questionInput, `${x.domain} · ${x.title}`, x.id)); if (questions.some(x => x.id === old)) questionInput.value = old; questionBox.textContent = questions.find(x => x.id === questionInput.value)?.prompt || ''; modeCatalog = await api('/training/modes'); const selectedMode = guideMode.value; guideMode.replaceChildren(); modeCatalog.forEach(x => option(guideMode, x.title, x.mode)); if (modeCatalog.some(x => x.mode === selectedMode)) guideMode.value = selectedMode; guideDescription.textContent = modeCatalog.find(x => x.mode === guideMode.value)?.description || ''; await loadGuidedHistory(); }
      if (tab === 'programme') await loadProgramme(); if (tab === 'workflows') await loadWorkflows(); if (tab === 'modules') await loadModules();
    },
    clear() { guideEpoch++; guided = null; guideDrafts.clear(); guideBody.replaceChildren(); guideHistory.replaceChildren(); guideMode.replaceChildren(); modeCatalog = []; guideDescription.textContent = ""; pending.clear(); drafts.clear(); contextId = null; exercises = []; stories = []; questions = []; prompt = coach = drill = report = programme = editStory = null; coachingForm.reset(); answerForm.reset(); storyForm.reset(); drillForm.reset(); [exerciseInput, storyInput, claimInput, questionInput, promptBox, feedback, history, reportBox, deliveryBox, scenarioBox, storyList, questionBox, drillResult, programmeBody, workBody, moduleList].forEach(x => x.replaceChildren()); promptBox.hidden = answerForm.hidden = true; }
  };
}
