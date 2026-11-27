// The admin console has no service worker; this file only removes one left behind by the viewer site.
//
// An admin address that once showed the viewer site (for example before the server knew its host name) gave the
// browser the viewer's service worker, which keeps answering every page there with the viewer's cached page. The
// browser checks /sw.js for updates on each visit: this version takes over, deletes that worker's caches, removes
// itself and reloads the open tabs, which then get the admin console from the server.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    const names = await caches.keys();
    await Promise.all(names.map((name) => caches.delete(name)));
    await self.registration.unregister();
    const tabs = await self.clients.matchAll({ type: "window" });
    for (const tab of tabs) tab.navigate(tab.url);
  })());
});
