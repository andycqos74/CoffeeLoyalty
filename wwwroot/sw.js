const CACHE = 'qosfc-loyalty-v1';
const PRECACHE = ['/join.html', '/logo.png', '/latte.png', '/lib/qrcode.js', '/manifest.json'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(PRECACHE).catch(() => {})));
  self.skipWaiting();
});

self.addEventListener('activate', e => {
  e.waitUntil(clients.claim());
});

self.addEventListener('fetch', e => {
  if (e.request.url.includes('/api/') || e.request.url.includes('/api/events/')) return;
  e.respondWith(
    caches.match(e.request).then(cached => cached || fetch(e.request))
  );
});
