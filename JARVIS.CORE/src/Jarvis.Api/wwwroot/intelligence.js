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
  const hold = field('Hold this run while I set up action permissions', 'input', { type: 'checkbox' });
  const submit = node('button', 'Start agent workflow', 'primary'); submit.disabled = true; form.append(submit);
  let pending = null;
  form.addEventListener('submit', async event => {
    event.preventDefault(); submit.disabled = true;
    try {
      const body = { workflow: workflow.value, brief: brief.value, allowCloudProcessing: consent.checked, holdForSetup: hold.checked };
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
  const domainsCard = node('div', '', 'card'); panel.append(domainsCard);
  domainsCard.append(node('h2', 'Domains'), node('p', 'Describe a domain to propose it. Review its access and missing features before approving an exact version with your owner key.'));
  const domainForm = document.createElement('form'); const requestText = document.createElement('textarea'); requestText.maxLength = 3000; requestText.rows = 3; requestText.required = true; requestText.setAttribute('aria-label', 'Describe a new domain');
  const cloudLabel = node('label', 'Allow cloud processing to generate this proposal'); const domainConsent = document.createElement('input'); domainConsent.type = 'checkbox'; domainConsent.required = true; cloudLabel.append(domainConsent);
  const sourcesLabel = node('label', 'Knowledge references: one HTTPS URL or import:UUID per line (up to six)'); const domainSources = document.createElement('textarea'); domainSources.rows = 2; domainSources.maxLength = 6100; domainSources.setAttribute('aria-label', 'Domain knowledge references'); sourcesLabel.append(domainSources);
  const proposeButton = node('button', 'Generate domain proposal'); domainForm.append(requestText, sourcesLabel, node('small', 'URLs are stored as references; JARVIS does not fetch or verify them. Files must already be saved in Imports.'), cloudLabel, proposeButton); domainsCard.append(domainForm);
  const domainList = node('div', '', 'list'); domainsCard.append(domainList); let domainPending = null;
  domainForm.addEventListener('submit', async event => { event.preventDefault(); proposeButton.disabled = true;
    try { const description = requestText.value.trim(); const knowledgeSources = domainSources.value.split('\n').map(x => x.trim()).filter(Boolean).map(x => x.startsWith('import:') ? { kind: 'import', reference: x.slice(7) } : { kind: 'url', reference: x });
      const fingerprint = JSON.stringify({ description, knowledgeSources }); if (!domainPending || domainPending.fingerprint !== fingerprint) domainPending = { requestId: crypto.randomUUID(), description, knowledgeSources, fingerprint };
      const { fingerprint: _, ...proposalRequest } = domainPending;
      const proposal = await api('/domains/proposals', { ...proposalRequest, allowCloudProcessing: domainConsent.checked });
      domainPending = null; notice(proposal.existingDomain ? 'An equivalent domain already exists. Review it below.' : 'Draft saved. Review its access and missing capabilities before owner approval.'); await load();
    } catch (error) { notice(error.message, true); } finally { proposeButton.disabled = false; }
  });
  function renderDomains(previews) {
    domainList.replaceChildren();
    for (const preview of previews) {
      const { definition, version } = preview; const row = node('div', '', 'list-item');
      row.append(node('h3', version.manifest.displayName + ' · ' + definition.status), node('p', version.manifest.description),
        node('p', 'Version ' + version.version + ' · ' + version.manifest.executionMode + ' · ' + preview.risk),
        node('p', 'Data access: ' + preview.memoryScope + (preview.sharesPersonalContext ? '; shared personal coaching context' : '; no shared personal coaching context')),
        node('p', preview.estimatedAiGbp === 0 ? 'Authored practice template: no AI cost.' : 'Assisted work requires separate cloud consent and the configured AI budget.'),
        node('p', 'Capabilities: ' + version.manifest.capabilities.map(c => c.id + ' (' + c.versionConstraint + ')').join(', ')));
      if (preview.unmetDependencies.length) row.append(node('p', 'Missing: ' + preview.unmetDependencies.join(' '), 'error'));
      if (version.manifest.studyGuidance) row.append(node('p', 'Study guidance: ' + version.manifest.studyGuidance));
      for (const source of version.manifest.knowledgeSources || []) row.append(node('p', 'Knowledge reference (' + source.kind + '): ' + source.reference));
      const memory = node('div'); row.append(button('View memory for ' + definition.key, async () => {
        const path = '/domains/' + encodeURIComponent(definition.key) + '/memory';
        const items = await api(path); memory.replaceChildren(node('h4', 'Domain memory'), node('p', 'Personal notes need consent for assisted work. Sensitive notes stay local. Notes expire within 90 days; disabling a domain keeps them.'));
        for (const item of items) { const entry = node('div'); entry.append(node('p', item.text), node('small', item.sensitivity + ' · ' + item.source.kind + ' · expires ' + new Date(item.expiresAt).toLocaleDateString()), button('Delete memory note', async () => { await api(path + '/' + item.id, undefined, 'DELETE'); entry.remove(); })); memory.append(entry); }
        if (definition.status === 'Active') {
          const note = document.createElement('textarea'); note.maxLength = 1200; note.setAttribute('aria-label', 'Memory note for ' + definition.key);
          const sensitivity = document.createElement('select'); sensitivity.setAttribute('aria-label', 'Memory sensitivity'); for (const value of ['Personal', 'Sensitive']) { const option = node('option', value); option.value = value; sensitivity.append(option); }
          memory.append(note, sensitivity, button('Save memory for ' + definition.key, async () => { await api(path, { requestId: crypto.randomUUID(), sourceKind: 'note', text: note.value, sensitivity: sensitivity.value, expiresAt: new Date(Date.now() + 30 * 86400000).toISOString() }); note.value = ''; notice('Domain memory saved for 30 days.'); }));
        }
      }), memory);
      const details = document.createElement('details'); details.append(node('summary', 'Exact manifest to approve'), node('pre', JSON.stringify(version.manifest, null, 2)), node('small', 'Manifest hash: ' + version.manifestHash)); row.append(details);
      const riskLabel = node('label', 'Approval risk class'); const approvalRisk = document.createElement('select'); approvalRisk.setAttribute('aria-label', 'Approval risk class');
      const riskChoices = preview.risk === 'General' ? ['General', 'Finance', 'Law', 'Health'] : [preview.risk];
      for (const risk of riskChoices) { const option = document.createElement('option'); option.value = risk; option.textContent = risk; approvalRisk.append(option); }
      approvalRisk.value = preview.risk; riskLabel.append(approvalRisk); row.append(riskLabel,
        node('small', preview.risk === 'General' ? 'You may apply a stricter risk class. Sensitive classes add policy restrictions and cannot be lowered later.' : 'This classification is inferred from the domain and cannot be lowered or replaced during approval.'));
      if (!preview.unmetDependencies.length && (definition.currentVersionId !== version.id || definition.status !== 'Active')) row.append(button('Approve and activate this version', async () => {
        if (!confirm('Activate ' + version.manifest.displayName + ' version ' + version.version + ' at ' + approvalRisk.value + ' risk with the access shown above?')) return;
        await api('/domains/' + encodeURIComponent(definition.key) + '/activate', { requestId: crypto.randomUUID(), expectedRevision: definition.revision, versionId: version.id, manifestHash: version.manifestHash, reason: 'Owner reviewed manifest and access preview', riskClass: approvalRisk.value }); await load();
      }));
      if (definition.status === 'Active') row.append(button('Disable domain', async () => { await api('/domains/' + encodeURIComponent(definition.key) + '/disable', { requestId: crypto.randomUUID(), expectedRevision: definition.revision }); await load(); }));
      domainList.append(row);
    }
  }
  function renderArtifact(container, output) {
    try {
      const artifact = JSON.parse(output);
      if (typeof artifact.summary === 'string' && Array.isArray(artifact.claims)) {
        container.append(node('p', artifact.summary));
        for (const drill of (artifact.drills || []).slice(0, 3)) container.append(node('p', drill));
        for (const claim of artifact.claims.slice(0, 100)) {
          const evidence = node('p', claim.kind + ': ' + claim.text + ' · Sources: ' + (claim.sources || []).slice(0, 12).join(', ')); evidence.style.overflowWrap = 'anywhere'; container.append(evidence);
        }
        if (artifact.evidenceLimit) container.append(node('small', artifact.evidenceLimit)); return;
      }
    } catch { /* Older plain-text or authored artifacts retain their existing view. */ }
    container.append(node('p', output));
  }
  async function load() {
    const [settings, saved, jobs, messages, catalog, domains, proposals, grants, specialists] = await Promise.all([api('/agents/controls'), api('/agents/schedules'), api('/agents/runs'), api('/agents/notifications'), api('/agents/catalog'), api('/domains'), api('/agents/actions'), api('/agents/tool-grants'), api('/agents/specialists')]);
    renderDomains(domains);
    const selected = workflow.value; workflows = catalog.map(entry => entry.key); workflow.replaceChildren();
    for (const kind of workflows) { const option = node('option', kind); option.value = kind; workflow.append(option); }
    if (workflows.includes(selected)) workflow.value = selected;
    submit.disabled = workflows.length === 0;
    controls.replaceChildren(node('h3', 'Pause and quiet hours'));
    controls.append(button(settings.paidAiPaused ?? settings.paused ? 'Resume paid AI' : 'Pause paid AI', async () => { await api('/agents/controls', { ...settings, paused: false, paidAiPaused: !(settings.paidAiPaused ?? settings.paused) }, 'PUT'); await load(); }));
    controls.append(button(settings.agentsPaused ?? settings.paused ? 'Resume agents' : 'Pause agents', async () => { await api('/agents/controls', { ...settings, paused: false, agentsPaused: !(settings.agentsPaused ?? settings.paused) }, 'PUT'); await load(); }));
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
      for (const task of view.tasks) { const details = document.createElement('details'); details.append(node('summary', `${task.role} · ${task.status}`)); if (task.output) renderArtifact(details, task.output); if (task.errorCode) details.append(node('p', task.errorCode)); row.append(details); }
      for (const grant of grants.filter(g => g.runId === view.run.id && g.grantedBy !== 'installed-artifact-policy')) {
        row.append(node('p', `${grant.capabilityId} · ${grant.revokedAt ? 'Revoked' : grant.preApproved ? 'Pre-approved reversible actions' : 'Exact approval required'} · up to ${grant.maximumCalls} calls · ${Number(grant.maximumUsd).toFixed(2)} USD per call (${(grant.maximumUsd * grant.maximumCalls).toFixed(2)} USD maximum across those calls, also subject to the global GBP budget)`));
        if (!grant.revokedAt) row.append(button('Revoke ' + grant.capabilityId, async () => { await api('/agents/tool-grants/' + grant.id, undefined, 'DELETE'); await load(); }));
      }
      if (view.run.status === 'AwaitingSetup') {
        const capability = document.createElement('select'); capability.setAttribute('aria-label', 'Agent action capability');
        const allowed = new Set(specialists.filter(s => view.tasks.some(t => t.role === s.role)).flatMap(s => s.allowedCapabilities).filter(id => !id.startsWith('core_')));
        for (const id of allowed) { const option = node('option', id); option.value = id; capability.append(option); }
        const label = node('label', 'Pre-approve reversible actions within existing device allow-lists'); const preapprove = document.createElement('input'); preapprove.type = 'checkbox'; label.append(preapprove);
        const grantId = crypto.randomUUID();
        row.append(node('p', 'This run is held. Add scoped permissions using your owner key, then start it. Existing device grants and their entity constraints are also required.'), capability, label,
          button('Grant capability for this run', async () => { await api(`/agents/runs/${view.run.id}/tool-grants`, { requestId: grantId, capabilityId: capability.value, preApproved: preapprove.checked }); await load(); }),
          button('Start configured run', async () => { await api(`/agents/runs/${view.run.id}/release`, {}); await load(); }));
      }
      for (const item of proposals.filter(p => p.proposal.runId === view.run.id)) {
        const action = node('div', '', 'card'); action.append(node('h4', item.proposal.capabilityId + ' · ' + item.status), node('p', 'Risk: ' + item.risk),
          node('pre', item.proposal.argumentsJson), node('small', 'Approval hash: ' + item.argumentsHash), node('p', 'Expires ' + new Date(item.proposal.expiresAt).toLocaleString()));
        if (item.status === 'AwaitingApproval') action.append(button('Approve exact agent action', async () => {
          if (!confirm('Approve this exact action?\n' + item.proposal.capabilityId + '\n' + item.proposal.argumentsJson)) return;
          await api(`/agents/actions/${item.proposal.id}/approve`, { argumentsHash: item.argumentsHash }); await load();
        }));
        if (item.status === 'AwaitingReceipt') action.append(node('p', 'Waiting for a verified device receipt. Completion has not been confirmed.'));
        row.append(action);
      }
      if (['Queued', 'Running', 'AwaitingSetup'].includes(view.run.status)) row.append(button('Cancel agent run', async () => { await api(`/agents/runs/${view.run.id}/cancel`, {}); await load(); }));
      if (['Failed', 'Expired', 'NeedsReconciliation'].includes(view.run.status)) {
        const retryId = crypto.randomUUID(); row.append(button('Retry agent run', async () => {
          if (!confirm('Start fresh work with the original cloud consent? This may incur AI cost. Unreconciled spending and external actions block retries.')) return;
          await api(`/agents/runs/${view.run.id}/retry`, { requestId: retryId }); await load();
        }));
      }
      row.append(button('Delete agent artifacts', async () => { if (!confirm('Delete this run’s private inputs and outputs? Minimal billing metadata remains.')) return; await api(`/agents/runs/${view.run.id}`, undefined, 'DELETE'); await load(); })); runs.append(row);
    }
  }
  card.append(button('Refresh agent status', load));
  return { load, clear() { domainPending = null; domainForm.reset(); domainList.replaceChildren(); pending = null; form.reset(); workflows = []; workflow.replaceChildren(); submit.disabled = true; controls.replaceChildren(); schedules.replaceChildren(); inbox.replaceChildren(); runs.replaceChildren(); } };
}
