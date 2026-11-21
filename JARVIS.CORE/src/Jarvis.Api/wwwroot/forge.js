// PF-09 browser Today view: read-only, the same block the spoken briefing uses. Text enters the DOM via textContent
// only (node()). Protected-division goal titles are already redacted by Core; nothing private is cached in storage.
export function initForgeToday({ api, node, button }) {
  const card = node('div', '', 'card'); card.id = 'forge-today';
  document.querySelector('[data-panel=briefing]').append(card);
  let generation = 0;
  function draw(today, message) {
    card.replaceChildren(node('h2', 'Forge today'), button('Reload Forge today', load));
    if (!today) { card.append(node('p', message, 'muted')); return; }
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
  function drawWeek(week) {
    if (!week) return;
    card.append(node('h3', 'This week'));
    const coverage = week.practiceCoverage === null || week.practiceCoverage === undefined ? 'unknown' : `${Math.round(week.practiceCoverage * 100)}%`;
    card.append(node('p', `${week.from} to ${week.to}${week.asOf ? ` · as of ${week.asOf}` : ''} · ${week.reviews} reviews · ${week.trainingAnswers} training answers · ${week.coachingAnswers} coaching answers · practice coverage ${coverage}`));
    card.append(node('p', 'Recorded practice only, not a skill score or weighted evidence coverage. Actual and planned minutes are unknown.', 'muted'));
    const unclassified = (week.unclassifiedReviews || 0) + (week.unclassifiedTrainingAnswers || 0) + (week.unclassifiedCoachingAnswers || 0);
    if (unclassified) card.append(node('p', `Unclassified provenance: ${unclassified} record${unclassified === 1 ? '' : 's'}; no division credit is inferred.`, 'muted'));
    const list = node('ul', '', 'list');
    for (const d of week.divisions) list.append(node('li', `${d.code}: ${d.practised ? 'recorded practice' : 'no recorded practice'}${d.hasActiveGoal ? ' · current active goal' : ''}`, 'list-item'));
    if (week.divisions.length) card.append(list);
  }
  // PF-09 evening review: the next day's suggestions (never a schedule until capacity is set).
  function drawTomorrow(tomorrow) {
    if (!tomorrow || !tomorrow.top.length) return;
    card.append(node('h3', `Tomorrow · day ${tomorrow.day} of ${tomorrow.days}`));
    const list = node('ol', '', 'list');
    for (const item of tomorrow.top) list.append(node('li', `${item.label} · ${item.minutes} min · ${item.why}`, 'list-item'));
    card.append(list);
  }
  async function load() {
    const token = ++generation;
    try {
      const today = await api('/forge/today');
      if (token !== generation) return; draw(today);
      try { const week = await api('/forge/week'); if (token === generation) drawWeek(week); }
      catch { if (token === generation) card.append(node('p', 'Weekly summary is unavailable. Reload to retry.', 'muted')); }
      try { const evening = await api('/forge/evening'); if (token === generation) drawTomorrow(evening.tomorrow); } catch { /* evening view is optional */ }
    } catch (error) {
      // Forge is off by default, and 404 until a programme exists: show a calm state, not an error banner.
      if (token === generation) draw(null, error?.status === 404
        ? 'Forge is off or has no programme yet.'
        : 'Forge today is unavailable. Reload to retry.');
    }
  }
  return { load, clear() { generation++; card.replaceChildren(); } };
}


