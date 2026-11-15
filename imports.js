export function initImports({ api, node, button, notice }) {
  const tab = node('button', 'CV & interview archive'); tab.dataset.tab = 'imports'; document.querySelector('nav').append(tab);
  const panel = document.createElement('section'); panel.dataset.panel = 'imports'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'); panel.append(card); card.append(node('h2', 'Import your evidence'), node('p', 'Import CV text or an authorised interview transcript. Source quotes establish provenance, not independent verification of a claim. Plain text stays in Core unless you separately request cloud processing.'));
  const form = document.createElement('form');
  function field(label, tag, props = {}) { const wrap = node('label', label); const value = document.createElement(tag); Object.assign(value, props); value.setAttribute('aria-label', label); wrap.append(value); form.append(wrap); return value; }
  const kind = field('Import type', 'select'); for (const value of ['cv', 'interview']) { const option = node('option', value); option.value = value; kind.append(option); }
  const title = field('Document title', 'input', { required: true, maxLength: 200 });
  const file = field('Load a plain text file', 'input', { type: 'file', accept: '.txt,.md,text/plain,text/markdown' });
  const text = field('Source text or interview transcript', 'textarea', { required: true, maxLength: 100000, rows: 10 });
  file.addEventListener('change', async () => { try { if (file.files[0]) { if (file.files[0].size > 400000) throw Error('Text file exceeds 400 KB.'); text.value = await file.files[0].text(); } } catch (error) { notice(error.message, true); } });
  const storage = field('I consent to storing this document in Core', 'input', { type: 'checkbox', required: true });
  const authorised = field('This interview capture and import were authorised', 'input', { type: 'checkbox' });
  form.append(node('button', 'Import into Core', 'primary')); let pending = null, generation = 0;
  form.addEventListener('submit', async event => { event.preventDefault(); event.submitter.disabled = true; try {
    const body = { kind: kind.value, title: title.value, text: text.value, confirmStorageConsent: storage.checked, confirmRecordingAuthorised: authorised.checked };
    const key = JSON.stringify(body); if (pending?.key !== key) pending = { key, id: crypto.randomUUID() };
    await api('/imports', { requestId: pending.id, ...body }); pending = null; form.reset(); await load(); notice('Source imported with its provenance hash.');
  } catch (error) { notice(error.message, true); } finally { event.submitter.disabled = false; } });
  const list = node('div', '', 'list'), detail = node('div', '', 'list-item'); card.append(form, button('Refresh imported sources', load), list, detail);
  let detailGeneration = 0;
  async function show(id) {
    const token = generation, view = ++detailGeneration;
    const current = () => token === generation && view === detailGeneration;
    detail.replaceChildren();
    const record = await api(`/imports/${id}`); if (!current()) return;
    detail.replaceChildren(node('h3', record.title));
    const source = document.createElement('textarea'); source.readOnly = true; source.rows = 10; source.value = record.text; source.setAttribute('aria-label', 'Imported source text'); detail.append(source);
    detail.append(node('small', `Source hash ${record.contentHash}`));
    const cloudWrap = node('label', 'I consent to sending this source to the cloud coach'); const cloud = document.createElement('input'); cloud.type = 'checkbox'; cloud.setAttribute('aria-label', 'Allow AI source analysis'); cloudWrap.append(cloud); let assessmentId = null;
    detail.append(cloudWrap, button('Analyse source with AI coach', async () => {
      if (!cloud.checked) throw Error('Explicit cloud-processing consent is required.');
      assessmentId ||= crypto.randomUUID(); await api(`/imports/${id}/assess`, { requestId: assessmentId, expectedRevision: record.revision, textRevision: record.revision, allowCloudProcessing: true }); if (current()) await show(id);
    }));
    const assessments = await api(`/imports/${id}/assessments`); if (!current()) return;
    for (const assessment of assessments) { detail.append(node('h3', 'AI source feedback'), node('p', assessment.summary)); for (const followUp of assessment.followUps) detail.append(node('p', followUp)); }
    if (record.kind === 'interview') {
      const review = await api(`/imports/${id}/review`); if (!current()) return;
      detail.append(node('h3', 'Transcript observations'), node('p', review.timingSource));
      for (const question of review.questions) detail.append(node('p', question));
      if (!review.questions.length) detail.append(node('p', 'No question-marked lines found. This is a text extraction heuristic.'));
      for (const drill of review.suggestedDrills) detail.append(node('p', drill));
    } else {
      detail.append(node('p', 'Select an exact claim in the source text, choose its story, then attach it.'));
      const stories = await api('/stories'); if (!current()) return;
      const choice = document.createElement('select'); choice.setAttribute('aria-label', 'Story for source claim');
      for (const story of stories) { const option = node('option', story.content.title); option.value = story.id; choice.append(option); }
      let range = null, operation = null;
      source.addEventListener('select', () => { if (source.selectionEnd > source.selectionStart) range = { start: source.selectionStart, length: source.selectionEnd - source.selectionStart }; });
      detail.append(choice, button('Attach selected CV claim to story', async () => {
        if (!range || !choice.value) throw Error('Select a source quote and a saved story first.');
        const story = stories.find(x => x.id === choice.value); const key = `${story.id}:${range.start}:${range.length}`;
        if (operation?.key !== key) operation = { key, id: crypto.randomUUID() };
        await api(`/imports/${id}/claims`, { requestId: operation.id, storyId: story.id, expectedStoryVersion: story.version, expectedDocumentRevision: record.revision, ...range });
        if (current()) { notice('Exact source claim attached. Review and practise its factual defence.'); await show(id); }
      }));
      const claims = await api(`/imports/${id}/claims`); if (!current()) return;
      for (const claim of claims) detail.append(node('blockquote', claim.quote));
    }
  }
  async function load() {
    const token = generation; const sources = await api('/imports'); if (token !== generation) return;
    list.replaceChildren(node('h3', 'Saved sources'));
    for (const source of sources) {
      const row = node('div', '', 'list-item'); row.append(button(`${source.title} · ${source.kind}`, () => show(source.id)), node('small', `${source.characters} characters · ${new Date(source.importedAt).toLocaleString()}`));
      row.append(button('Delete source and derived claims', async () => { await api(`/imports/${source.id}?revision=${source.revision}`, undefined, 'DELETE'); detailGeneration++; detail.replaceChildren(); await load(); notice('Source and linked claim/assessment content removed.'); })); list.append(row);
    }
  }
  return { load, clear() { generation++; pending = null; form.reset(); list.replaceChildren(); detail.replaceChildren(); } };
}
