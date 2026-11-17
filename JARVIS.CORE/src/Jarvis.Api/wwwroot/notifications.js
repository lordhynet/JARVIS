export function initNotifications({ api, node, button, notice }) {
  const panel = document.querySelector('[data-panel=settings]'); const card = node('div', '', 'card');
  card.append(node('h2', 'Private browser notifications'), node('p', 'Opt in to generic notifications when a workflow is ready. Personal results stay in your authenticated inbox. Delivery follows quiet hours and expiry. Browser and operating-system background delivery must be tested on your device.'));
  panel.append(card); let generation = 0;
  function supported() { if (!window.isSecureContext || !('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) throw Error('This browser needs a trusted HTTPS connection and Web Push support.'); }
  function bytes(value) { const raw = atob(value.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - value.length % 4) % 4)); return Uint8Array.from(raw, c => c.charCodeAt(0)); }
  card.append(button('Enable browser notifications', async () => {
    supported(); const token = generation;
    const permission = await Notification.requestPermission(); if (permission !== 'granted') throw Error('Notification permission was not granted.');
    const settings = await api('/push/status'); if (!settings.ready) throw Error('Browser push must be configured in appsettings.json.');
    const registration = await navigator.serviceWorker.register('/sw.js'); await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.getSubscription() || await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: bytes(settings.publicKey) });
    if (token !== generation) { await subscription.unsubscribe(); return; }
    const data = subscription.toJSON(); const id = await api('/push/subscriptions', { endpoint: data.endpoint, p256dh: data.keys.p256dh, auth: data.keys.auth });
    if (token !== generation) { await subscription.unsubscribe(); return; }
    localStorage.setItem('jarvis-push-id', id); notice('Browser notifications enabled. You can disable them here or in browser settings.');
  }), button('Disable browser notifications', async () => { await stop(); notice('Browser notifications disabled for this device.'); }));
  async function stop() {
    generation++;
    const id = localStorage.getItem('jarvis-push-id'); localStorage.removeItem('jarvis-push-id');
    const remove = id ? api(`/push/subscriptions/${id}`, undefined, 'DELETE').catch(() => {}) : Promise.resolve();
    if ('serviceWorker' in navigator) {
      const registration = await navigator.serviceWorker.getRegistration();
      if (registration) { const subscription = await registration.pushManager.getSubscription(); if (subscription) await subscription.unsubscribe(); for (const notification of await registration.getNotifications()) notification.close(); }
    }
    await remove;
  }
  return { clear() { stop().catch(() => {}); } };
}
