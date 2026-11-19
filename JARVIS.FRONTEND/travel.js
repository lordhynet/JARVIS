export function initTravel({ api, node, button, notice }) {
  const card = node('div', '', 'card'); card.id = 'travel-routes';
  document.querySelector('[data-panel=briefing]').append(card);
  let generation = 0, routes = [], lines = [], editing = null, pending = null;
  const days = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
  function field(parent, title, type, value) {
    const label = node('label', title), input = node('input'); input.type = type; input.value = value; label.append(input); parent.append(label); return input;
  }
  function draw() {
    card.replaceChildren(node('h2', 'My routes'), node('p', 'Save up to ten London routes. Alerts are allowed only during the days and times you choose, including overnight windows. Times follow Europe/London. Public line updates can appear on your lock screen; saved route names stay private.'));
    card.append(button('Reload routes', async () => { editing = null; pending = null; await load(); }));
    const list = node('div', '', 'list'); card.append(list);
    for (const route of routes) {
      const row = node('div', '', 'list-item');
      row.append(node('h3', route.name), node('p', `${route.enabled ? 'Enabled' : 'Disabled'} · ${route.lines.map(l => l.name).join(', ')}`),
        node('p', route.windows.map(w => `${w.days.map(d => typeof d === 'number' ? days[d] : d).join(', ')} ${w.start}–${w.end}`).join('; ')),
        button('Edit route', () => { editing = route; pending = null; draw(); }),
        button('Delete route', async () => { const token = generation; await api(`/travel/routes/${route.id}?revision=${route.revision}`, undefined, 'DELETE'); if (token === generation) { editing = null; pending = null; await load(); } })); list.append(row);
    }
    if (!lines.length) { card.append(node('p', 'The TfL line catalogue is unavailable. Reload routes to try again.', 'muted')); return; }
    const form = node('form'); form.id = 'travel-form'; form.append(node('h3', editing ? 'Edit saved route' : 'Add a route'));
    const name = field(form, 'Route name', 'text', editing?.name || ''); name.required = true; name.maxLength = 80;
    const picker = node('fieldset'); picker.append(node('legend', 'TfL lines (choose up to 12)'));
    const lineInputs = lines.map(line => { const label = node('label', '', 'inline'), input = node('input'); input.type = 'checkbox'; input.value = line.id; input.checked = !!editing?.lines.some(l => l.id === line.id); label.append(input, document.createTextNode(line.name)); picker.append(label); return input; });
    form.append(picker);
    const stops = field(form, 'Saved stop IDs (optional, comma separated)', 'text', editing?.stops.map(s => s.id).join(', ') || ''); stops.maxLength = 400;
    const enabledLabel = node('label', '', 'inline'), enabled = node('input'); enabled.type = 'checkbox'; enabled.checked = editing?.enabled ?? true; enabledLabel.append(enabled, document.createTextNode('Route enabled')); form.append(enabledLabel);
    const windows = node('div'); form.append(windows); const windowRows = [];
    function addWindow(value = { days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'], start: '07:00', end: '09:00' }) {
      const row = node('fieldset'); row.append(node('legend', 'Alert window'));
      const checks = days.map((day, index) => { const label = node('label', '', 'inline'), input = node('input'); input.type = 'checkbox'; input.value = day; input.checked = value.days.includes(day) || value.days.includes(index); label.append(input, document.createTextNode(day)); row.append(label); return input; });
      const start = field(row, 'Start time', 'time', value.start), end = field(row, 'End time', 'time', value.end); start.required = end.required = true;
      const entry = { row, checks, start, end }; windowRows.push(entry);
      row.append(button('Remove window', () => { row.remove(); windowRows.splice(windowRows.indexOf(entry), 1); })); windows.append(row);
    }
    for (const value of editing?.windows || [undefined]) addWindow(value);
    form.append(button('Add alert window', () => { if (windowRows.length >= 8) throw Error('Choose at most eight windows.'); addWindow(); }));
    const save = node('button', 'Save route', 'primary'); save.type = 'submit'; form.append(save);
    if (editing) form.append(button('Cancel editing', () => { editing = null; pending = null; draw(); }));
    form.addEventListener('submit', async event => {
      event.preventDefault(); const token = generation; save.disabled = true;
      try {
        const body = { expectedRevision: editing?.revision ?? null, name: name.value.trim(), lineIds: lineInputs.filter(i => i.checked).map(i => i.value), stopIds: stops.value.split(',').map(s => s.trim()).filter(Boolean),
          windows: windowRows.map(w => ({ days: w.checks.filter(i => i.checked).map(i => i.value), start: w.start.value, end: w.end.value })), enabled: enabled.checked, timeZone: 'Europe/London' };
        if (!body.lineIds.length || body.lineIds.length > 12 || !body.windows.length || body.windows.some(w => !w.days.length || w.start === w.end)) throw Error('Choose 1–12 lines and at least one window with days and different start and end times.');
        const hash = JSON.stringify(body); if (pending?.hash !== hash) pending = { hash, requestId: crypto.randomUUID() };
        await api(editing ? `/travel/routes/${editing.id}` : '/travel/routes', { ...body, requestId: pending.requestId }, editing ? 'PUT' : 'POST');
        if (token !== generation) return; editing = null; pending = null; await load(); notice('Route saved.');
      } catch (error) { if (token === generation) notice(error.message, true); } finally { save.disabled = false; }
    }); card.append(form);
  }
  async function load() {
    const token = ++generation;
    const saved = await api('/travel/routes'); if (token !== generation) return; routes = saved;
    try { const catalogue = await api('/travel/lines'); if (token !== generation) return; lines = catalogue; }
    catch (error) { if (token !== generation) return; lines = []; notice(error.message, true); }
    draw();
  }
  return { load, clear() { generation++; routes = []; lines = []; editing = pending = null; card.replaceChildren(); } };
}
