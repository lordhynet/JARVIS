export function initIntelligence({ api, node, button, notice }) {
  const tab = node('button', 'Proactive agents'); tab.dataset.tab = 'agents'; document.querySelector('nav').append(tab);
  const panel = document.createElement('section'); panel.dataset.panel = 'agents'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'); panel.append(card); card.append(node('h2', 'Your proactive workspace'), node('p', 'Create a scoped goal or schedule. Research uses public search; private coaching context is sent only with your cloud-processing consent.'));
  const controls = node('div'); const schedules = node('div'); const runs = node('div', '', 'list'); const inbox = node('div', '', 'list');
  const form = document.createElement('form');
  function field(label, tag = 'input', props = {}) { const wrap = node('label', label); const input = document.createElement(tag); Object.assign(input, props); input.setAttribute('aria-label', label); wrap.append(input); form.append(wrap); return input; }
  let workflows = [];
  const workflow = field('Workflow', 'select');
  const brief = field('Scoped goal or preparation brief', 'textarea', { maxLength: 2000, rows: 3 });
  const consent = field('Allow cloud processing for this goal and its selected sources', 'input', { type: 'checkbox' });
  const submit = node('button', 'Start agent workflow', 'primary'); submit.disabled = true; form.append(submit);
  let pending = null;
  form.addEventListener('submit', async event => {
    event.preventDefault(); submit.disabled = true;
    try {
      const body = { workflow: workflow.value, brief: brief.value, allowCloudProcessing: consent.checked };
      const key = JSON.stringify(body); if (pending?.key !== key) pending = { key, id: crypto.randomUUID() };
      await api('/agents/runs', { requestId: pending.id, ...body }); pending = null; notice('Durable agent workflow accepted.'); await load();
    } catch (error) { notice(error.message, true); } finally { submit.disabled = false; }
  });
  const time = field('Daily schedule time', 'input', { type: 'time', value: '09:00' });
  const title = field('Schedule title', 'input', { maxLength: 120, value: 'Daily interview practice' });
  const scheduleButton = button('Save daily schedule for this workflow', async () => {
    if (!time.value) throw Error('Choose a daily time.');
    await api('/agents/schedules', { requestId: crypto.randomUUID(), title: title.value, workflow: workflow.value,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'Europe/London', localTime: time.value, enabled: true, allowCloudProcessing: consent.checked }); await load();
  });
  card.append(form, scheduleButton, controls, schedules, inbox, runs);
  async function load() {
    const [settings, saved, jobs, messages, catalog] = await Promise.all([api('/agents/controls'), api('/agents/schedules'), api('/agents/runs'), api('/agents/notifications'), api('/agents/catalog')]);
    const selected = workflow.value; workflows = catalog.map(entry => entry.key); workflow.replaceChildren();
    for (const kind of workflows) { const option = node('option', kind); option.value = kind; workflow.append(option); }
    if (workflows.includes(selected)) workflow.value = selected;
    submit.disabled = workflows.length === 0;
    controls.replaceChildren(node('h3', 'Pause and quiet hours'));
    controls.append(button(settings.paused ? 'Resume all paid AI and agents' : 'Pause all paid AI and agents', async () => { await api('/agents/controls', { ...settings, paused: !settings.paused }, 'PUT'); await load(); }));
    for (const kind of workflows) {
      const paused = (settings.pausedWorkflows || []).includes(kind);
      controls.append(button(`${paused ? 'Resume' : 'Pause'} ${kind}`, async () => {
        const values = new Set(settings.pausedWorkflows || []); if (paused) values.delete(kind); else values.add(kind);
        await api('/agents/controls', { ...settings, pausedWorkflows: [...values] }, 'PUT'); await load();
      }));
    }
    const quiet = document.createElement('form'); const start = document.createElement('input'); start.type = 'time'; start.value = settings.quietStart; start.setAttribute('aria-label', 'Quiet hours start');
    const end = document.createElement('input'); end.type = 'time'; end.value = settings.quietEnd; end.setAttribute('aria-label', 'Quiet hours end'); const save = node('button', 'Save quiet hours');
    quiet.append(node('p', `Quiet hours · ${settings.timeZone}`), start, end, save); quiet.addEventListener('submit', async e => { e.preventDefault(); try { await api('/agents/controls', { ...settings, quietStart: start.value, quietEnd: end.value }, 'PUT'); await load(); } catch (error) { notice(error.message, true); } }); controls.append(quiet);
    schedules.replaceChildren(node('h3', 'Durable schedules'));
    for (const schedule of saved) {
      const row = node('div', '', 'list-item'); row.append(node('strong', schedule.title), node('p', `${schedule.workflow} · ${schedule.localTime} ${schedule.timeZone} · ${schedule.enabled ? 'Enabled' : 'Disabled'}`));
      row.append(button(schedule.enabled ? 'Disable schedule' : 'Enable schedule', async () => { await api('/agents/schedules', { requestId: schedule.id, expectedRevision: schedule.revision, ...schedule, enabled: !schedule.enabled }); await load(); })); schedules.append(row);
    }
    inbox.replaceChildren(node('h3', 'Private notification inbox'));
    for (const message of messages) { const row = node('div', '', 'list-item'); row.append(node('strong', message.title), node('small', new Date(message.createdAt).toLocaleString())); if (!message.read) row.append(button('Mark read', async () => { await api(`/agents/notifications/${message.id}/read`, {}); await load(); })); inbox.append(row); }
    runs.replaceChildren(node('h3', 'Runs and specialist tasks'));
    for (const view of jobs) {
      const row = node('div', '', 'list-item'); row.append(node('h3', `${view.run.workflow} · ${view.run.status}`));
      for (const task of view.tasks) { const details = document.createElement('details'); details.append(node('summary', `${task.role} · ${task.status}`)); if (task.output) details.append(node('p', task.output)); if (task.errorCode) details.append(node('p', task.errorCode)); row.append(details); }
      if (['Queued', 'Running'].includes(view.run.status)) row.append(button('Cancel agent run', async () => { await api(`/agents/runs/${view.run.id}/cancel`, {}); await load(); }));
      row.append(button('Delete agent artifacts', async () => { if (!confirm('Delete this run’s private inputs and outputs? Minimal billing metadata remains.')) return; await api(`/agents/runs/${view.run.id}`, undefined, 'DELETE'); await load(); })); runs.append(row);
    }
  }
  card.append(button('Refresh agent status', load));
  return { load, clear() { pending = null; form.reset(); workflows = []; workflow.replaceChildren(); submit.disabled = true; controls.replaceChildren(); schedules.replaceChildren(); inbox.replaceChildren(); runs.replaceChildren(); } };
}
