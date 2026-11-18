// Customer pages service worker. Pages are network-first (their words and photos are edited in Admin -> Pages),
// falling back to the cache when offline; other files are cache-first. API calls always go to the network.
const CACHE = 'loyalty-v2';
const PRECACHE = ['/join.html', '/css/base.css', '/theme.css', '/lib/qrcode.js'];

self.addEventListener('install', e => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(PRECACHE).catch(() => {})));
  self.skipWaiting();
});

self.addEventListener('activate', e => {
  e.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== CACHE && !k.startsWith('till')).map(k => caches.delete(k))))
    .then(() => clients.claim()));
});

self.addEventListener('fetch', e => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname.startsWith('/api/')) return;
  const isPage = e.request.mode === 'navigate' || url.pathname === '/join.html' || url.pathname.startsWith('/card/');
  const isStyle = url.pathname === '/theme.css' || url.pathname.startsWith('/css/');
  if (isPage || isStyle) {
    e.respondWith(fetch(e.request).then(res => {
      if (res.ok) { const copy = res.clone(); caches.open(CACHE).then(c => c.put(e.request, copy)); }
      return res;
    }).catch(() => caches.match(e.request)));
    return;
  }
  e.respondWith(caches.match(e.request).then(cached => cached || fetch(e.request)));
});
