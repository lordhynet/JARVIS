// No fetch caching: authenticated Core responses and credentials never enter a service-worker cache.
self.addEventListener('push', event => {
  let tag = 'jarvis-update';
  try { const candidate = event.data?.json()?.tag; if (typeof candidate === 'string' && /^[0-9a-f-]{36}$/i.test(candidate)) tag = candidate; } catch { /* A payload is optional. */ }
  event.waitUntil(self.registration.showNotification('JARVIS update', {
    body: 'Open your private JARVIS inbox for a new update.', tag, renotify: false, data: { path: '/' }
  }));
});
self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil(clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async windows => {
    const current = windows.find(window => new URL(window.url).origin === self.location.origin);
    if (current) return current.focus();
    return clients.openWindow('/');
  }));
});
