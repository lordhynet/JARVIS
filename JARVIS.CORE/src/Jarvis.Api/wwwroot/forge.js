// PF-09 browser Today view: read-only, the same block the spoken briefing uses. Text enters the DOM via textContent
// only (node()). Protected-division goal titles are already redacted by Core; nothing private is cached in storage.
export function initForgeToday({ api, node, button, notice = () => {} }) {
  const card = node('div', '', 'card'); card.id = 'forge-today';
  document.querySelector('[data-panel=briefing]').append(card);
  let generation = 0;
  function draw(today, message) {
    card.replaceChildren(node('h2', 'Forge today'), button('Reload Forge today', load));
    if (!today) { card.append(node('p', message, 'muted')); return; }
    // PF-02: adds the sixteen divisions as registry drafts only; each still needs approval under Domains.
    card.append(button('Install Forge division drafts', async () => {
      const result = await api('/forge/divisions/drafts', { includeOptional: false });
      const drafted = result.filter(x => x.status === 'drafted').length, existing = result.filter(x => x.status === 'existing').length;
      notice(`${drafted} division drafts added, ${existing} already present. Approve each one under Domains; nothing was activated.`);
    }));
    if (today.status === 'not-started') {
      card.append(node('p', `Forge starts in ${today.startsInDays} day${today.startsInDays === 1 ? '' : 's'}. Day 1 of ${today.days}.`));
    } else if (today.status === 'graduated') {
      card.append(node('p', `Programme calendar ended (${today.days} days).`));
    } else {
      card.append(node('p', `Day ${today.day} of ${today.days}${today.phase ? ` · ${today.phase}` : ''}${today.status === 'paused' ? ' · paused' : ''}`));
    }
    card.append(node('p', `${today.dueReviews} review card${today.dueReviews === 1 ? '' : 's'} due today.`, 'muted'));
    if (!today.top.length) {
      if (today.status === 'active') card.append(node('p', 'Nothing to suggest yet. Tag a goal with a Forge division or add review cards.', 'muted'));
      return;
    }
    card.append(node('h3', today.scheduled ? 'Planned in your free time' : 'Top three suggestions'));
    if (!today.scheduled) card.append(node('p', 'These activities are suggestions in priority order.', 'muted'));
    const list = node('ol', '', 'list');
    for (const item of today.top) list.append(node('li', `${item.label} · ${item.minutes} min · ${item.why}`, 'list-item'));
    card.append(list);
  }
  // J4: counts of existing records per division; coverage stays "unknown" when there is nothing to judge.
  function drawActivity(week, target, heading = 'This week') {
    if (!week) return;
    target.append(node('h3', heading));
    const coverage = week.practiceCoverage === null || week.practiceCoverage === undefined ? 'unknown' : `${Math.round(week.practiceCoverage * 100)}%`;
    target.append(node('p', `${week.from} to ${week.to}${week.asOf ? ` · as of ${week.asOf}` : ''} · ${week.reviews} reviews · ${week.trainingAnswers} training answers · ${week.coachingAnswers} coaching answers · practice coverage ${coverage}`));
    target.append(node('p', 'Recorded practice only, not a skill score or weighted evidence coverage. Actual and planned minutes are unknown.', 'muted'));
    const unclassified = (week.unclassifiedReviews || 0) + (week.unclassifiedTrainingAnswers || 0) + (week.unclassifiedCoachingAnswers || 0);
    if (unclassified) target.append(node('p', `Unclassified provenance: ${unclassified} record${unclassified === 1 ? '' : 's'}; no division credit is inferred.`, 'muted'));
    const list = node('ul', '', 'list');
    for (const d of week.divisions) list.append(node('li', `${d.code}: ${d.practised ? 'recorded practice' : 'no recorded practice'}${d.hasActiveGoal ? ' · current active goal' : ''}`, 'list-item'));
    if (week.divisions.length) target.append(list);
  }
  function drawTomorrow(evening, target) {
    drawActivity(evening.today, target, 'Today’s recorded practice');
    const tomorrow = evening.tomorrow;
    target.append(node('h3', `Tomorrow${evening.tomorrowDate ? ` · ${evening.tomorrowDate}` : ''}`));
    if (tomorrow.status === 'paused') { target.append(node('p', 'Programme paused; no activities are suggested.')); return; }
    if (tomorrow.status === 'not-started') { target.append(node('p', `Programme has not started; ${tomorrow.startsInDays} day${tomorrow.startsInDays === 1 ? '' : 's'} until Day 1.`)); return; }
    if (tomorrow.status === 'graduated') { target.append(node('p', 'Programme calendar ended.')); return; }
    target.append(node('p', `Day ${tomorrow.day} of ${tomorrow.days} · suggestions only.`));
    if (!tomorrow.top.length) { target.append(node('p', 'No suggestions for tomorrow yet.', 'muted')); return; }
    const list = node('ol', '', 'list');
    for (const item of tomorrow.top) list.append(node('li', `${item.label} · ${item.minutes} min · ${item.why}`, 'list-item'));
    target.append(list);
  }
  async function load() {
    const token = ++generation;
    try {
      const today = await api('/forge/today');
      if (token !== generation) return; draw(today);
      const week = node('section'), evening = node('section'); card.append(week, evening);
      const optional = async (path, target, render, failure) => {
        try { const value = await api(path); if (token === generation) render(value, target); }
        catch { if (token === generation) target.replaceChildren(node('p', failure, 'muted')); }
      };
      // Start both before awaiting either, so a cleared weekly continuation cannot start a new evening read.
      await Promise.all([
        optional('/forge/week', week, drawActivity, 'Weekly summary is unavailable. Reload to retry.'),
        optional('/forge/evening', evening, drawTomorrow, 'Evening review is unavailable. Reload to retry.')
      ]);
    } catch (error) {
      // Forge is off by default, and 404 until a programme exists: show a calm state, not an error banner.
      if (token === generation) draw(null, error?.status === 404
        ? 'Forge is off or has no programme yet.'
        : 'Forge today is unavailable. Reload to retry.');
    }
  }
  return { load, clear() { generation++; card.replaceChildren(); } };
}
