export function initPlanning({ api, node, button, notice }) {
  const tab = node('button', 'Goals & automation'); tab.dataset.tab = 'planning'; document.querySelector('nav').append(tab);
  const panel = document.createElement('section'); panel.dataset.panel = 'planning'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'); panel.append(card); card.append(node('h2', 'Goals and accountable automation'));
  card.append(node('p', 'A due goal produces one review workflow. Set its next review when updating progress. Paused, completed and cancelled goals do not start new work.'));
  const form = document.createElement('form'); let pending = null, editing = null, generation = 0;
  function field(label, tag, props = {}) { const wrap = node('label', label); const value = document.createElement(tag); Object.assign(value, props); value.setAttribute('aria-label', label); wrap.append(value); form.append(wrap); return value; }
  const title = field('Goal title', 'input', { required: true, maxLength: 120 });
  const brief = field('Goal context and next step', 'textarea', { required: true, maxLength: 2000, rows: 3 });
  const workflow = field('Goal workflow', 'select');
  for (const value of ['interview-preparation', 'daily-coaching', 'practice-reminder', 'career-plan', 'personal-plan', 'research-review']) { const option = node('option', value); option.value = value; workflow.append(option); }
  const due = field('Next goal review', 'input', { type: 'datetime-local', required: true });
  const deadline = field('Goal deadline', 'input', { type: 'datetime-local', required: true });
  const status = field('Goal state', 'select'); for (const value of ['Active', 'Paused', 'Completed', 'Cancelled']) { const option = node('option', value); option.value = value; status.append(option); }
  const consent = field('Allow cloud processing for goal reviews', 'input', { type: 'checkbox' });
  form.append(node('button', 'Save persistent goal', 'primary'));
  form.addEventListener('submit', async event => {
    event.preventDefault(); const submit = event.submitter; submit.disabled = true;
    try {
      const body = { title: title.value, brief: brief.value, workflow: workflow.value, nextReviewAt: new Date(due.value).toISOString(), deadline: new Date(deadline.value).toISOString(), status: status.value, allowCloudProcessing: consent.checked, expectedRevision: editing?.revision ?? null };
      const fingerprint = JSON.stringify(body); if (pending?.fingerprint !== fingerprint) pending = { fingerprint, id: editing?.id || crypto.randomUUID() };
      await api('/goals', { requestId: pending.id, ...body }); pending = editing = null; form.reset(); await load(); notice('Goal saved in Core.');
    } catch (error) { notice(error.message, true); } finally { submit.disabled = false; }
  });
  const operations = node('div', '', 'list-item');
  const goals = node('div', '', 'list'), triggers = node('div', '', 'list'), charges = node('div', '', 'list'); card.append(form, goals, operations);
  card.append(button('Check connected calendar now', async () => { const count = await api('/agents/calendar/sync', {}); notice(`${count} calendar changes ingested.`); await load(); }), triggers);
  card.append(node('h3', 'Provider billing reconciliation'), node('p', 'Use a verified provider billing record to resolve uncertain charges or a rate mismatch. This acknowledges a charge; it never replays a provider request. Wait ten minutes after dispatch. Do not paste API keys.'), charges, button('Refresh goals and charges', load));
  function local(at) { const date = new Date(at); return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16); }
  async function load() {
    const token = generation; const [saved, events, ledger, health] = await Promise.all([api('/goals'), api('/agents/triggers'), api('/usage/charges'), api('/operations')]); if (token !== generation) return;
    operations.replaceChildren(node('h3', 'Core operations'), node('p', `Queued tasks: ${health.queuedTasks} · running: ${health.runningTasks} · expired leases: ${health.expiredLeases} · uncertain charges: ${health.uncertainCharges} · monthly committed: ${health.monthCommittedUsd}`));
    goals.replaceChildren(node('h3', 'Persistent goals'));
    for (const goal of saved) {
      const row = node('div', '', 'list-item'); row.append(node('strong', goal.title), node('p', `${goal.status} · review ${new Date(goal.nextReviewAt).toLocaleString()} · ${goal.lastRunId ? 'Review workflow created' : 'Waiting'}`), node('p', goal.brief));
      row.append(button('Update goal progress', () => { editing = goal; pending = null; title.value = goal.title; brief.value = goal.brief; workflow.value = goal.workflow; due.value = local(goal.nextReviewAt); deadline.value = local(goal.deadline); status.value = goal.status; consent.checked = goal.allowCloudProcessing; form.scrollIntoView({ block: 'start' }); }),
        button('Delete goal context', async () => { await api(`/goals/${goal.id}?revision=${goal.revision}`, undefined, 'DELETE'); if (editing?.id === goal.id) { editing = pending = null; form.reset(); } await load(); })); goals.append(row);
    }
    triggers.replaceChildren(node('h3', 'Calendar and event triggers'));
    for (const trigger of events) triggers.append(node('p', `${trigger.source} · ${trigger.status} · revision ${trigger.revision} · ${new Date(trigger.dueAt).toLocaleString()}`));
    charges.replaceChildren();
    for (const charge of ledger.filter(x => x.actualUsd === null || x.reconciliationRequired)) {
      const row = node('div', '', 'list-item'); row.append(node('strong', `${charge.provider} · ${charge.model}`), node('p', `${charge.id} · reserved $${charge.reservedUsd} · actual ${charge.actualUsd === null ? 'unknown' : '$' + charge.actualUsd}`));
      const reconcile = document.createElement('form');
      function input(label, props) { const wrap = node('label', label), value = document.createElement('input'); Object.assign(value, props); value.setAttribute('aria-label', label); wrap.append(value); reconcile.append(wrap); return value; }
      const actual = input('Verified charge in USD', { type: 'number', min: '0', max: '10000', step: '0.000001', required: true });
      const evidence = input('Provider billing reference', { maxLength: 500, required: true });
      const confirmation = input('Type CONFIRM VERIFIED CHARGE', { maxLength: 40, required: true }); const submit = node('button', 'Reconcile verified charge'); reconcile.append(submit); let operation = null;
      reconcile.addEventListener('submit', async event => { event.preventDefault(); submit.disabled = true; try {
        const body = { actualUsd: Number(actual.value), evidence: evidence.value, confirmation: confirmation.value }; const key = JSON.stringify(body);
        if (operation?.key !== key) operation = { key, id: crypto.randomUUID() };
        await api(`/usage/charges/${charge.id}/reconcile`, { requestId: operation.id, ...body }); notice('Verified charge recorded. Existing dispatch IDs remain protected against replay.'); await load();
      } catch (error) { notice(error.message, true); } finally { submit.disabled = false; } }); row.append(reconcile); charges.append(row);
    }
    if (!charges.children.length) charges.append(node('p', 'No uncertain charges require reconciliation.'));
  }
  return { load, clear() { generation++; pending = editing = null; form.reset(); goals.replaceChildren(); triggers.replaceChildren(); charges.replaceChildren(); operations.replaceChildren(); } };
}
