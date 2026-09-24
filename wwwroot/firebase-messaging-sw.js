/* QueueSystem FCM fallback service worker. */
importScripts(
  "https://www.gstatic.com/firebasejs/12.19.0/firebase-app-compat.js",
  "https://www.gstatic.com/firebasejs/12.19.0/firebase-messaging-compat.js",
  "/firebase-config.js"
);

const cfg = globalThis.QUEUE_FIREBASE;
if (cfg && cfg.enabled && cfg.config && cfg.config.apiKey && cfg.config.projectId && cfg.config.messagingSenderId && cfg.config.appId) {
  try {
    firebase.initializeApp(cfg.config);
    const messaging = firebase.messaging();
    messaging.onBackgroundMessage(payload => {
      const data = payload?.data || {};
      const lang = data.lang === "en" ? "en" : "ar";
      const title = data.title || (lang === "en" ? "Queue update" : "تحديث الدور");
      const body = data.body || "";
      const link = new URL(data.link || "/track/", self.location.origin).href;
      const eventKey = [data.clientNumber || "x", data.status || "update", data.nearRemaining || "", data.liveEventSeq || "0"].join("-");
      return self.registration.showNotification(title, {
        body,
        icon: "/icons/icon-192.png",
        badge: "/icons/icon-192.png",
        tag: "queue-" + eventKey,
        renotify: true,
        requireInteraction: true,
        dir: lang === "ar" ? "rtl" : "ltr",
        lang,
        data: { link, eventKey }
      });
    });
  } catch (e) {
    console.error("[QueueSystem FCM SW] initialization failed", e);
  }
}

self.addEventListener("notificationclick", event => {
  event.notification.close();
  const link = event.notification?.data?.link;
  if (!link) return;
  event.waitUntil((async () => {
    const list = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of list) {
      try { await client.navigate(link); } catch (_) {}
      if ("focus" in client) return client.focus();
    }
    return self.clients.openWindow(link);
  })());
});
