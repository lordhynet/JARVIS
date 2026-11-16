export function initPrivacy({ api, node, button, notice, onErased, onReopened }) {
  const tab = node('button', 'Privacy & data'); tab.dataset.tab = 'privacy'; document.querySelector('nav').append(tab);
  const panel = document.createElement('section'); panel.dataset.panel = 'privacy'; panel.hidden = true; document.getElementById('workspace').append(panel);
  const card = node('div', '', 'card'); panel.append(card); card.append(node('h2', 'Your Core data'));
  card.append(node('p', 'Export your records, review retention, or erase this single-owner Core database. Backups, exported files, provider-held data and other devices’ local caches are outside this erasure.'));
  const statusBox = node('div', '', 'list-item'), retention = node('div', '', 'list-item'), erase = node('div', '', 'list-item'), jobs = node('div', '', 'list');
  let status = null, preview = null, eraseId = null, retentionId = null, retentionRequest = null, pendingPreview = null, generation = 0;
  card.append(statusBox, button('Download Core data export', async () => {
    const token = generation; const data = await api('/privacy/export'); if (token !== generation) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
    const link = document.createElement('a'); link.href = url; link.download = `jarvis-core-${new Date().toISOString().slice(0, 10)}.json`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    notice('Export downloaded. It contains personal data; store it privately.');
  }), retention, erase, jobs, button('Refresh privacy status', load));
  function input(parent, label) { const wrap = node('label', label); const value = document.createElement('input'); value.maxLength = 40; value.required = true; value.setAttribute('aria-label', label); wrap.append(value); parent.append(wrap); return value; }
  function render() {
    statusBox.replaceChildren(node('strong', status.erased ? 'Core data erased — writes blocked' : 'Core active'), node('p', `Data generation ${status.epoch} · privacy revision ${status.revision}`));
    retention.replaceChildren(node('h3', 'Retention preview'));
    if (preview) {
      retention.append(node('p', `Automatic retention: ${preview.policy.enabled ? 'enabled' : 'disabled'}. Settings live in root appsettings.json under Privacy.Retention.`));
      retention.append(node('p', `Ended sessions: ${preview.policy.sessionDays} days · answers: ${preview.policy.answerDays} · voice transcripts: ${preview.policy.voiceDays} · provider cache: ${preview.policy.providerCacheDays} · finished artifacts: ${preview.policy.artifactDays}. Recent corrections reset answer age. Retiring an ended session also purges its linked practice content.`));
      retention.append(node('pre', JSON.stringify(preview.eligible, null, 2)));
      if (pendingPreview) retention.append(button('Discard pending retry and refresh preview', async () => {
        retentionId = retentionRequest = pendingPreview = null; await load();
      }));
      if (!status.erased) {
        const form = document.createElement('form'); const confirmation = input(form, 'Type APPLY RETENTION to apply this preview'); const apply = node('button', 'Apply retention preview', 'danger'); form.append(apply);
        form.addEventListener('submit', async event => {
          event.preventDefault(); apply.disabled = true;
          try {
            if (confirmation.value !== 'APPLY RETENTION') throw Error('Type APPLY RETENTION exactly.');
            const token = generation;
            retentionId ||= crypto.randomUUID();
            pendingPreview ||= preview;
            retentionRequest ||= { requestId: retentionId, asOf: preview.asOf, policyFingerprint: preview.policyFingerprint, confirmation: confirmation.value };
            await api('/privacy/retention/apply', retentionRequest); if (token !== generation) return;
            retentionId = retentionRequest = pendingPreview = null; notice('Retention committed. Minimal replay and spending records remain.'); await load();
          } catch (error) { notice(error.message, true); } finally { apply.disabled = false; }
        }); retention.append(form);
      }
    }
    erase.replaceChildren(node('h3', status.erased ? 'Reopen with a new Core key' : 'Erase Core database content'));
    const form = document.createElement('form'); const phrase = status.erased ? 'START A NEW CORE' : 'ERASE MY CORE DATA';
    erase.append(node('p', status.erased ? 'Rotate Jarvis.ApiKey in appsettings.json. Disconnect this tab, reconnect using the new key, then explicitly reopen. Old in-flight operations remain fenced out; paid AI stays paused.' :
      'This removes sessions, stories, learning content, coaching, programme notes, devices, private workflow artifacts and caches from Core. Minimal spending records remain to prevent duplicate charges and budget resets. Erasure blocks writes until you rotate the Core API key and reopen. Export first if you need a copy.'));
    const confirmation = input(form, `Type ${phrase}`); const submit = node('button', status.erased ? 'Reopen empty Core' : 'Erase Core data', 'danger'); form.append(submit);
    form.addEventListener('submit', async event => {
      event.preventDefault(); submit.disabled = true;
      try {
        if (confirmation.value !== phrase) throw Error(`Type ${phrase} exactly.`);
        const token = generation;
        if (status.erased) {
          await api('/privacy/reopen', { expectedRevision: status.revision, confirmation: confirmation.value }); if (token !== generation) return;
          await onReopened(); notice('Core reopened. Paid AI and agents remain paused until you explicitly resume them.');
        } else {
          eraseId ||= crypto.randomUUID(); await api('/privacy/erase', { requestId: eraseId, expectedRevision: status.revision, confirmation: confirmation.value });
          if (token !== generation) return; eraseId = null; onErased(); notice('Core database content erased. Rotate the Core key before reopening.');
        }
        await load();
      } catch (error) { notice(error.message, true); } finally { submit.disabled = false; }
    }); erase.append(form);
  }
  async function load() {
    const token = generation; const [current, candidate, history] = await Promise.all([api('/privacy/status'), api('/privacy/retention/preview'), api('/privacy/jobs')]);
    if (token !== generation) return; status = current; preview = pendingPreview || candidate; render(); jobs.replaceChildren(node('h3', 'Content-free privacy job history'));
    for (const job of history) { const row = node('div', '', 'list-item'); row.append(node('strong', `${job.kind} · ${new Date(job.completedAt).toLocaleString()}`), node('pre', JSON.stringify(job.removed, null, 2))); jobs.append(row); }
  }
  return { load, show() { tab.click(); }, clear() { generation++; status = preview = null; eraseId = retentionId = retentionRequest = pendingPreview = null; [statusBox, retention, erase, jobs].forEach(x => x.replaceChildren()); } };
}
