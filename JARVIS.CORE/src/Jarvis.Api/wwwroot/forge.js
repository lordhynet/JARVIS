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
  async function load() {
    const token = ++generation;
    try {
      const today = await api('/forge/today');
      if (token === generation) draw(today);
    } catch (error) {
      // Forge is off by default, and 404 until a programme exists: show a calm state, not an error banner.
      if (token === generation) draw(null, error?.status === 404
        ? 'Forge is off or has no programme yet.'
        : 'Forge today is unavailable. Reload to retry.');
    }
  }
  return { load, clear() { generation++; card.replaceChildren(); } };
}
