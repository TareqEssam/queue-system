# تفعيل إشعارات Firebase (والصفحة مغلقة / في الخلفية)

هذا الدليل فقط لتفعيل **FCM**.  
الكود جاهز مسبقاً؛ تحتاج إعداد مشروع Firebase مرة واحدة ثم لصق القيم.

**المدة المتوقعة:** 15–25 دقيقة  
**التكلفة:** خطة Spark المجانية كافية عادةً لهذا الاستخدام

---

## 1) ماذا ستحصل؟

| الحالة | بدون Firebase | مع Firebase |
|--------|----------------|-------------|
| الصفحة مفتوحة | صوت + اهتزاز + بانر + إشعار متصفح | نفس الشيء + أدق |
| الصفحة في الخلفية | محدود حسب المتصفح | إشعار نظام |
| الصفحة مغلقة | غالباً لا | إشعار نظام عبر Service Worker |
| iPhone | يحتاج إضافة للشاشة الرئيسية ثم الإذن | نفس الشرط |

أحداث الإرسال من السيرفر:
- اقتراب الدور (متبقي 2 أو 1)
- استدعاء الرقم
- تخطي مؤقت
- تحويل للمدير
- (ومع الحالات النهائية عبر التحديث المحلي + FCM عند التفعيل)

---

## 2) إنشاء مشروع Firebase

1. افتح: https://console.firebase.google.com  
2. **Add project** / إضافة مشروع  
3. اختر اسماً (مثال: `queue-system`)  
4. يمكن تعطيل Google Analytics إن لم تحتاجه  
5. أنشئ المشروع وانتظر حتى يكتمل

---

## 3) إضافة تطبيق Web

1. داخل المشروع: أيقونة **Web** (`</>`)  
2. App nickname: `queue-web`  
3. لا تشترط Firebase Hosting  
4. بعد التسجيل ستظهر كتل مثل:

```js
const firebaseConfig = {
  apiKey: "AIza...",
  authDomain: "xxx.firebaseapp.com",
  projectId: "xxx",
  storageBucket: "xxx.appspot.com",
  messagingSenderId: "123456789",
  appId: "1:123456789:web:abc"
};
```

**انسخ هذه القيم** — ستلصقها في الخطوة 6.

---

## 4) مفتاح VAPID (Web Push)

1. أيقونة الترس → **Project settings**  
2. تبويب **Cloud Messaging**  
3. قسم **Web Push certificates**  
4. **Generate key pair**  
5. انسخ المفتاح **العام** (Public key) — هذا هو `vapidKey`

---

## 5) Service Account (للسيرفر فقط — سرّي)

1. Project settings → تبويب **Service accounts**  
2. **Generate new private key** → Confirm  
3. سيُحمَّل ملف JSON  
4. أعد تسميته إلى:

```text
firebase-service-account.json
```

5. ضعه **بجانب** `QueueSystem.exe` (مجلد `publish-win`):

```text
publish-win\
  QueueSystem.exe
  appsettings.json
  firebase-service-account.json   ← هنا
  wwwroot\
  cloudflared.exe                 (إن وُجد)
```

> **تحذير:** هذا الملف مفتاح خاص. لا ترفعه على GitHub أو ترسله للعامة.

---

## 6) تعبئة إعدادات العميل

افتح الملف:

```text
publish-win\wwwroot\firebase-config.js
```

(أو من المصدر: `wwwroot/firebase-config.js`)

الصق كما يلي (استبدل القيم بقيمك):

```js
window.QUEUE_FIREBASE = {
  enabled: true,
  vapidKey: "الصق_مفتاح_VAPID_العام_هنا",
  config: {
    apiKey: "AIza...",
    authDomain: "your-project.firebaseapp.com",
    projectId: "your-project-id",
    storageBucket: "your-project.appspot.com",
    messagingSenderId: "123456789012",
    appId: "1:123456789012:web:abcdef"
  }
};
```

احفظ الملف.

---

## 7) تعبئة إعدادات السيرفر

افتح:

```text
publish-win\appsettings.json
```

عدّل قسم Firebase:

```json
"Firebase": {
  "Enabled": true,
  "ProjectId": "your-project-id",
  "ServiceAccountJsonPath": "firebase-service-account.json"
}
```

- `ProjectId` = نفس `projectId` في firebase-config.js  
- المسار نسبي لمجلد التشغيل (بجانب exe)

احفظ الملف.

---

## 8) إعادة التشغيل والاختبار

1. أغلق `QueueSystem.exe` إن كان يعمل  
2. شغّله من جديد  
3. في السجلات يجب ألا يظهر: `FCM معطّل`  
4. من هاتف (يفضّل Android أولاً):
   - افتح رابط المتابعة `https://PORTAL/track/?ticket=...&token=...`
   - اضغط **تفعيل التنبيهات**
   - وافق على الإذن
5. أغلق الصفحة بالكامل  
6. من الباحث: استدعِ نفس الرقم  
7. يجب أن يصل إشعار على الهاتف

### iPhone
1. افتح الرابط في Safari  
2. مشاركة → **إضافة إلى الشاشة الرئيسية**  
3. افتح من الأيقونة  
4. فعّل التنبيهات  
5. قد تحتاج إعدادات النظام → الإشعارات → السماح للتطبيق

---

## 9) قائمة تحقق

- [ ] مشروع Firebase + تطبيق Web  
- [ ] VAPID key  
- [ ] `firebase-service-account.json` بجانب exe  
- [ ] `firebase-config.js` → `enabled: true` + كل الحقول  
- [ ] `appsettings.json` → `Firebase:Enabled: true` + `ProjectId`  
- [ ] إعادة تشغيل البرنامج  
- [ ] تفعيل التنبيهات من صفحة المتابعة  
- [ ] اختبار استدعاء والصفحة مغلقة  

---

## 10) أعطال شائعة

| المشكلة | الحل |
|---------|------|
| لا يصل إشعار والصفحة مغلقة | لم يُضغط «تفعيل التنبيهات» أو رُفض الإذن |
| FCM معطّل في السجل | `Enabled` ليس true أو ملف Service Account غير موجود |
| خطأ 403 من Google | Service Account من مشروع مختلف عن ProjectId |
| iPhone لا يعمل | يجب الإضافة للشاشة الرئيسية + HTTPS (النفق) |
| يعمل فقط والصفحة مفتوحة | طبيعي بدون FCM؛ أكمل الخطوات 5–7 |

---

## 11) ملاحظات أمان

- ملف Service Account = سرّ تشغيل السيرفر فقط  
- قيم `firebase-config.js` (apiKey وغيرها) عامة للعميل — هذا طبيعي في Firebase Web  
- لا تشارك `firebase-service-account.json` أبداً  

---

**بعد إكمال هذا الدليل تصبح إشعارات الخلفية مفعّلة.**  
راجع أيضاً: `01_دليل_التشغيل_مع_النفق.md` إن لم يكن الرابط العام جاهزاً (FCM يحتاج HTTPS في أغلب المتصفحات).
