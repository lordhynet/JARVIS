const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('service worker displays only fresh public travel bodies and keeps other categories private', async () => {
  const handlers = {}, shown = [];
  const self = { addEventListener: (name, fn) => handlers[name] = fn, registration: { showNotification: async (title, options) => shown.push({ title, ...options }) } };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../../src/Jarvis.Api/wwwroot/sw.js'), 'utf8'), { self, TextEncoder, Date, Promise });
  async function push(payload) { let pending; handlers.push({ data: { json: () => payload }, waitUntil: p => pending = p }); await pending; }
  const tag = '12345678-1111-4111-8111-111111111111';
  await push({ category: 'Travel', body: 'Jubilee: Severe Delays. Signal failure.', expiresAt: new Date(Date.now() + 60000).toISOString(), tag });
  expect(shown[0]).toMatchObject({ title: 'JARVIS travel', body: 'Jubilee: Severe Delays. Signal failure.', tag });
  await push({ category: 'Agent', title: 'PRIVATE', body: 'PRIVATE RESULT', tag });
  await push({ category: 'Unknown', body: 'PRIVATE SUMMARY' });
  expect(shown.slice(1).map(n => n.body)).toEqual(['Open your private JARVIS inbox for a new update.', 'Open your private JARVIS inbox for a new update.']);
  await push({ category: 'Travel', body: 'Late alert', expiresAt: new Date(Date.now() - 1).toISOString(), tag });
  await push({ category: 'Travel', body: 'x'.repeat(241), expiresAt: new Date(Date.now() + 60000).toISOString(), tag });
  await push({ category: 'Travel', body: 'Missing expiry', tag });
  expect(shown).toHaveLength(3);
});
