/* QueueSystem standard Web Push service worker. */

function notificationEventKey(data) {
  return [data.clientNumber || "x", data.status || "update", data.nearRemaining || "", data.liveEventSeq || "0"].join("-");
}

async function showQueueNotification(data) {
  const lang = data.lang === "en" ? "en" : "ar";
  const title = data.title || (lang === "en" ? "Queue update" : "تحديث الدور");
  const body = data.body || "";
  const link = new URL(data.link || "/track/", self.location.origin).href;
  const eventKey = notificationEventKey(data);

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
}

self.addEventListener("push", event => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch (_) {
    try { data = event.data ? { body: event.data.text() } : {}; } catch (_) {}
  }
  event.waitUntil(showQueueNotification(data));
});

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
