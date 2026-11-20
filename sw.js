'use strict';

// オフラインで動かすための Service Worker。
// 中身を変えたら CACHE_NAME の版を上げる（app.js の APP_VERSION と合わせる）。
const CACHE_NAME = 'setpress-cam-0.1.1';
const FILES = [
  './',
  './index.html',
  './style.css',
  './app.js',
  './manifest.webmanifest',
  './icons/icon-192.png',
  './icons/icon-512.png',
  './icons/apple-touch-icon.png',
];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE_NAME).then((c) => c.addAll(FILES)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

// まずネットから取り、取れたらキャッシュも新しくする（更新がすぐ届く）。
// ネットが無い・3 秒で返事が無いときはキャッシュを返す（オフラインでも開ける）。
const TIMEOUT_MS = 3000;

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET' || new URL(req.url).origin !== self.location.origin) return;
  e.respondWith((async () => {
    const cache = await caches.open(CACHE_NAME);
    const network = fetch(req, { cache: 'no-cache' }).then((res) => {
      if (res.ok) cache.put(req, res.clone());
      return res;
    });
    const timeout = new Promise((resolve) => setTimeout(resolve, TIMEOUT_MS, null));
    try {
      const res = await Promise.race([network, timeout]);
      if (res) return res;
    } catch (err) { /* オフライン */ }
    const hit = await cache.match(req, { ignoreSearch: true });
    return hit || network;
  })());
});
