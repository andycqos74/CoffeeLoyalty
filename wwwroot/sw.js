const CACHE = 'loyalty-{{identity.slug}}-v{{configVersion}}';
const PRECACHE = ['/join.html', '{{asset.logo}}', '{{asset.hero}}', '/lib/qrcode.js', '/manifest.json',
                  '/theme.css?v={{configVersion}}'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(PRECACHE).catch(() => {})));
  self.skipWaiting();
});

self.addEventListener('activate', e => {
  // Drop every older cache so a re-brand cannot leave a stale shell behind.
  e.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => clients.claim()));
});

self.addEventListener('fetch', e => {
  if (e.request.url.includes('/api/') || e.request.url.includes('/api/events/')) return;
  e.respondWith(
    caches.match(e.request).then(cached => cached || fetch(e.request))
  );
});
