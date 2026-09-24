# رابط ثابت + إشعارات والصفحة مغلقة

## الجزء 1 — رابط نفق ثابت (Cloudflare Named Tunnel)

الرابط المجاني `trycloudflare.com` يتغير عند كل تشغيل. للرابط **الثابت** بدون فتح بورت:

### المتطلبات
- حساب Cloudflare مجاني: https://dash.cloudflare.com/sign-up
- `cloudflared.exe` بجانب البرنامج
- (مستحسن) نطاق فرعي على Cloudflare — أو استخدم `*.cfargotunnel.com` مؤقتاً

### الخطوات مرة واحدة (على جهاز فيه متصفح)

```text
1) cloudflared.exe tunnel login
   → يفتح المتصفح للموافقة على الحساب

2) cloudflared.exe tunnel create queue-system
   → ينشئ نفقاً اسمه queue-system ويحفظ بيانات الاعتماد

3) أنشئ ملف cloudflared-config.yml بجانب QueueSystem.exe:
```

```yaml
tunnel: queue-system
credentials-file: C:\QueueSystem\<TUNNEL-ID>.json

ingress:
  - hostname: queue.your-domain.com   # إن وُجد نطاق على Cloudflare
    service: http://127.0.0.1:5000
  - service: http_status:404
```

إن لم يكن لديك نطاق بعد، يمكن تشغيل النفق بالاسم فقط وسيظهر معرف النفق؛ أو اربط نطاقاً مجانياً لاحقاً.

```text
4) في appsettings.json:
   "Tunnel": {
     "Enabled": true,
     "Mode": "named",
     "NamedTunnel": "queue-system",
     "ConfigPath": "cloudflared-config.yml",
     "CloudflaredPath": "cloudflared.exe"
   }

5) (مع نطاق) من لوحة Cloudflare DNS:
   Type: CNAME
   Name: queue
   Target: <TUNNEL-ID>.cfargotunnel.com
   Proxy: ON

6) شغّل QueueSystem.exe
   الرابط الثابت: https://queue.your-domain.com
```

### بدون نطاق مدفوع؟
- يمكن استخدام نطاق مجاني (مثل من Freenom سابقاً أو أي نطاق رخيص) وإضافته إلى Cloudflare.
- أو الإبقاء على `Mode: quick` للتجربة (الرابط يتغير).

---

## الجزء 2 — إشعارات FCM والصفحة مغلقة / في الخلفية

### أ) إعداد Firebase (مجاني — Spark)

1. أنشئ مشروعاً: https://console.firebase.google.com
2. أضف تطبيق **Web** وانسخ الإعدادات العامة (`apiKey`, `projectId`, …)
3. Project settings → **Cloud Messaging** → Web Push certificates → Generate key pair (VAPID)
4. Project settings → **Service accounts** → Generate new private key  
   احفظ الملف كـ `firebase-service-account.json` بجانب `QueueSystem.exe`  
   **لا ترفع هذا الملف للعامة.**

### ب) إعداد السيرفر (`appsettings.json`)

```json
"Firebase": {
  "Enabled": true,
  "ProjectId": "your-project-id",
  "ServiceAccountJsonPath": "firebase-service-account.json",
  "VapidKey": "YOUR_PUBLIC_VAPID_KEY"
}
```

### ج) إعداد العميل (`wwwroot/firebase-config.js`)

```js
window.QUEUE_FIREBASE = {
  enabled: true,
  vapidKey: "YOUR_PUBLIC_VAPID_KEY",
  config: {
    apiKey: "...",
    authDomain: "...",
    projectId: "...",
    storageBucket: "...",
    messagingSenderId: "...",
    appId: "..."
  }
};
```

### د) سلوك الإشعارات

| الحالة | الآلية |
|--------|--------|
| الصفحة مفتوحة | SignalR لحظي + إشعار محلي |
| الصفحة في الخلفية / مغلقة | FCM عبر Service Worker |
| iPhone | يجب «إضافة إلى الشاشة الرئيسية» ثم تفعيل التنبيهات من الأيقونة |

### هـ) حدود الاستخدام
- FCM على الخطة المجانية كافٍ جداً لإشعارات الطابور (إرسال عند الاستدعاء/التخطي فقط وليس باستمرار).
- لا يوجد polling ثقيل على Firestore في هذا التصميم.

---

## قائمة تحقق

- [ ] Named Tunnel يعمل و DNS يشير للنفق
- [ ] `Firebase:Enabled = true` + ملف Service Account موجود
- [ ] `firebase-config.js` مملوء و `enabled: true`
- [ ] تجربة من هاتف: تسجيل → تفعيل التنبيهات → إغلاق الصفحة → استدعاء الرقم من الشباك → وصول الإشعار
