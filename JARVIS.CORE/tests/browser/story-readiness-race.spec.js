const { test, expect } = require('@playwright/test');

// Exercise the actual module with native DOM and deferred synthetic API fixtures.
// No owner records or provider calls; awaiting both loads makes the old-response race deterministic.
for (const mode of ['overlap', 'clear', 'unavailable', 'older-version']) {
  test(`story readiness refresh preserves the current view: ${mode}`, async ({ page }) => {
    await page.goto('/');
    const result = await page.evaluate(async mode => {
      const { initModules } = await import('/modules.js');
      document.body.replaceChildren();
      const nav = document.createElement('nav');
      const workspace = document.createElement('div'); workspace.id = 'workspace';
      document.body.append(nav, workspace);
      const node = (tag, text, className) => {
        const value = document.createElement(tag); value.textContent = text;
        if (className) value.className = className;
        return value;
      };
      const id = '11111111-1111-4111-8111-111111111111';
      const story = (title, version) => ({ id, version, content: { title, companyProject: 'Fixture', serviceFeature: 'Fixture service' } });
      const readiness = (version, score) => [{ storyId: id, version, score, level: 'draft', checks: [], followUps: [] }];
      let releaseFirst, firstStarted;
      const firstResponse = new Promise(resolve => { releaseFirst = resolve; });
      const started = new Promise(resolve => { firstStarted = resolve; });
      let storyCalls = 0, readinessCalls = 0;
      const module = initModules({
        api: async path => {
          if (path === '/stories') {
            storyCalls++;
            return [mode === 'overlap' && storyCalls === 1 ? story('Older snapshot', 1) : story('Current snapshot', 2)];
          }
          if (path === '/stories/readiness') {
            readinessCalls++;
            if (mode === 'unavailable') throw Error('Fixture checklist unavailable');
            if (mode === 'older-version') return readiness(1, 6);
            if (readinessCalls === 1) { firstStarted(); return firstResponse; }
            return readiness(2, 50);
          }
          throw Error(`Unexpected fixture API: ${path}`);
        },
        node, notice() {}, on() {}, selectedSession: () => null, ensureSession: async () => null,
        button: (text, run) => { const b = node('button', text); b.type = 'button'; b.addEventListener('click', run); return b; }
      });
      const first = module.load('stories');
      if (mode === 'overlap' || mode === 'clear') {
        await started;
        if (mode === 'clear') module.clear();
        else await module.load('stories');
        releaseFirst(readiness(1, 6));
      }
      await first;
      const panel = document.querySelector('[data-panel="stories"]');
      return { titles: [...panel.querySelectorAll('h3')].map(x => x.textContent), text: panel.textContent };
    }, mode);

    expect(result.titles).toEqual(mode === 'clear' ? [] : ['Current snapshot']);
    if (mode === 'overlap') {
      expect(result.text.match(/Interview readiness/g)).toHaveLength(1);
      expect(result.text).toContain('Interview readiness 50%');
    } else expect(result.text).not.toContain('Interview readiness');
  });
}
