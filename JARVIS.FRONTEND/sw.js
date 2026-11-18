// No fetch caching: authenticated Core responses and credentials never enter a service-worker cache.
self.addEventListener('push', event => {
  let tag = 'jarvis-update', title = 'JARVIS update', body = 'Open your private JARVIS inbox for a new update.';
  try {
    const payload = event.data?.json();
    if (typeof payload?.tag === 'string' && /^[0-9a-f-]{36}$/i.test(payload.tag)) tag = payload.tag;
    if (payload?.category === 'Travel') {
      // The encrypted Core payload contains only public line text. Late travel pushes are discarded.
      const expires = Date.parse(payload.expiresAt);
      if (!Number.isFinite(expires) || expires <= Date.now() || typeof payload.body !== 'string' || !payload.body.length ||
          new TextEncoder().encode(payload.body).length > 240 || /[\u0000-\u001f\u007f-\u009f]/.test(payload.body)) return;
      title = 'JARVIS travel'; body = payload.body;
    }
  } catch { /* A payload is optional; personal content is never rendered. */ }
  event.waitUntil(self.registration.showNotification(title, {
    body, tag, renotify: false, data: { path: '/' }
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
