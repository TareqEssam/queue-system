# دليل التشغيل والنشر — مع النفق (رابط عام للعملاء على الجوال)

**نظام الطوابير المحلي (Portable)**  
الإصدار: 1.0  
الغرض: تشغيل البرنامج على جهاز الكمبيوتر بدون صلاحيات أدمن، مع رابط HTTPS عام يصل إليه العملاء من إنترنت هواتفهم **بدون فتح أي منفذ على الراوتر**.

---

## 0) ماذا ستحصل في النهاية؟

| العنصر | النتيجة |
|--------|---------|
| برنامج يعمل على الجهاز | `QueueSystem.exe` |
| قاعدة بيانات محلية | ملف `queue.db` بجانب البرنامج |
| رابط عام ثابت أو مؤقت | مثل `https://....trycloudflare.com` أو نطاقك |
| صفحات النظام | `/form/` تسجيل · `/track/` متابعة · `/employee/` باحث · `/manager/` مدير |

---

## 1) المتطلبات (مرة واحدة)

### 1.1 جهاز التشغيل (المكتب)
- Windows 10 أو أحدث (64-bit) — موصى به
- اتصال إنترنت مستقر (خروج فقط — Outbound)
- **لا تحتاج** صلاحيات Administrator لتثبيت النظام نفسه
- مساحة حرة تقريباً 200 ميجابايت

### 1.2 جهاز التطوير (إن لم يكن لديك الملف التنفيذي جاهزاً)
- .NET 8 SDK من: https://dotnet.microsoft.com/download/dotnet/8.0  
  اختر **SDK 8.0** لنظامك (Windows x64).

### 1.3 أدوات اختيارية للإشعارات والرابط الثابت
- حساب Cloudflare مجاني (للنفق)
- مشروع Firebase مجاني (للإشعارات والصفحة مغلقة) — يُشرح في القسم 7

---

## 2) فك الضغط وترتيب المجلد

1. احصل على الملف المضغوط `QueueSystem_portable_source.zip`.
2. فك الضغط إلى مجلد واضح، مثال:
   ```text
   C:\QueueSystem\
   ```
3. يجب أن ترى داخل المجلد ملفات مثل:
   ```text
   QueueSystem.csproj
   Program.cs
   appsettings.json
   wwwroot\
   docs\
   publish.ps1
   publish.sh
   ```

> لا تشغّل من داخل مجلد Downloads إن أمكن؛ انقل المجلد إلى `C:\QueueSystem`.

---

## 3) بناء النسخة المحمولة (Portal التنفيذي)

### الطريقة أ — من Windows (موصى بها)

1. افتح **PowerShell**.
2. نفّذ:
   ```powershell
   cd C:\QueueSystem
   powershell -ExecutionPolicy Bypass -File .\publish.ps1
   ```
3. بعد انتهاء البناء ستجد المجلد:
   ```text
   C:\QueueSystem\publish-win\
   ```
   وبداخله تقريباً:
   ```text
   QueueSystem.exe
   appsettings.json
   wwwroot\
   ...
   ```

### الطريقة ب — من Linux / macOS (بناء لنسخة Windows)

```bash
cd QueueSystem
chmod +x publish.sh
./publish.sh
```

الناتج: مجلد `publish-win` انسخه إلى جهاز Windows.

### الطريقة ج — تشغيل تطوير سريع (بدون نشر)

```powershell
cd C:\QueueSystem
dotnet restore
dotnet run --urls http://127.0.0.1:5000
```

للإنتاج اليومي استخدم `QueueSystem.exe` من `publish-win`.

---

## 4) إعداد الأمان قبل أول تشغيل (إلزامي)

1. افتح الملف:
   ```text
   publish-win\appsettings.json
   ```
2. غيّر القيمة:
   ```json
   "Security": {
     "JwtSecret": "ضع_هنا_نصاً_عشوائياً_طويلاً_جداً_لا_يقل_عن_64_حرفاً",
     "TokenExpireHours": 8
   }
   ```
3. احفظ الملف.

> بدون تغيير `JwtSecret` يبقى النظام أقل أماناً. لا تشارك هذا الملف علناً.

---

## 5) تثبيت وتشغيل النفق (Cloudflare Tunnel)

### 5.1 لماذا النفق؟
- العملاء على إنترنت الجوال يحتاجون رابطاً يبدأ بـ `https://`
- الجهاز **لا يفتح أي بورت وارد**
- البرنامج يتصل خارجاً فقط إلى Cloudflare

### 5.2 تحميل cloudflared

1. افتح: https://github.com/cloudflare/cloudflared/releases  
2. حمّل: `cloudflared-windows-amd64.exe`  
3. أعد تسميته إلى: `cloudflared.exe`  
4. ضعه **بجانب** `QueueSystem.exe` داخل `publish-win`:

```text
publish-win\
  QueueSystem.exe
  cloudflared.exe
  appsettings.json
  wwwroot\
```

### 5.3 وضع النفق في الإعدادات

افتح `appsettings.json` وتأكد من:

```json
"Tunnel": {
  "Enabled": true,
  "Mode": "quick",
  "CloudflaredPath": "cloudflared.exe",
  "Protocol": "http2"
}
```

| Mode | المعنى |
|------|--------|
| `quick` | رابط مؤقت `trycloudflare.com` (يتغير عند كل إعادة تشغيل) — مناسب للتجربة |
| `named` | رابط ثابت عبر نفق مسمّى — انظر القسم 5.5 |

### 5.4 التشغيل والحصول على الرابط

1. انقر مرتين على `QueueSystem.exe`.
2. انتظر حتى تظهر نافذة البرنامج (أو سجلات التشغيل).
3. ابحث عن سطر مشابه لـ:
   ```text
   =================================================
     الرابط العام: https://xxxx-xx-xx.trycloudflare.com
   =================================================
   ```
4. انسخ هذا الرابط — هذا هو **رابط البوابة (Portal)**.

### 5.5 رابط ثابت (Named Tunnel) — مرة واحدة

1. افتح PowerShell من مجلد `publish-win`:
   ```powershell
   .\cloudflared.exe tunnel login
   ```
   أكمل الموافقة من المتصفح.

2. أنشئ نفقاً:
   ```powershell
   .\cloudflared.exe tunnel create queue-system
   ```

3. انسخ ملف الاعتمادات `.json` الناتج إلى مجلد `publish-win`.

4. أنشئ ملف `cloudflared-config.yml` (يمكنك البدء من `cloudflared-config.yml.example`):
   ```yaml
   tunnel: queue-system
   credentials-file: C:\QueueSystem\publish-win\TUNNEL-ID.json

   ingress:
     - hostname: queue.your-domain.com
       service: http://127.0.0.1:5000
     - service: http_status:404
   ```

5. في لوحة Cloudflare DNS:
   - Type: **CNAME**
   - Name: `queue`
   - Target: `<TUNNEL-ID>.cfargotunnel.com`
   - Proxy: **ON**

6. في `appsettings.json`:
   ```json
   "Tunnel": {
     "Enabled": true,
     "Mode": "named",
     "NamedTunnel": "queue-system",
     "ConfigPath": "cloudflared-config.yml",
     "CloudflaredPath": "cloudflared.exe"
   }
   ```

7. أعد تشغيل `QueueSystem.exe`.  
   الرابط الثابت يصبح: `https://queue.your-domain.com`

---

## 6) روابط البوابة (بعد ظهور الرابط العام)

استبدل `https://PORTAL` بالرابط الذي ظهر لك:

| الاستخدام | الرابط |
|-----------|--------|
| الصفحة الرئيسية | `https://PORTAL/` |
| تسجيل عميل | `https://PORTAL/form/` |
| متابعة رقم | يصدر تلقائياً بعد التسجيل |
| الباحث | `https://PORTAL/employee/` |
| المدير | `https://PORTAL/manager/` |
| فحص الصحة | `https://PORTAL/api/health` |

### حساب المدير الافتراضي (أول تشغيل فقط)
| الحقل | القيمة |
|-------|--------|
| Username | `admin` |
| Password | `[لا توجد كلمة مرور افتراضية — استخدم QUEUE_ADMIN_PASSWORD]` |

**غيّر كلمة المرور فوراً** من واجهة المدير بعد إنشاء حساب آمن جديد.

---

## 7) تفعيل الإشعارات (والصفحة مغلقة / في الخلفية)

### 7.1 ما يعمل بدون Firebase؟
- الصفحة مفتوحة: تنبيهات صوتية + اهتزاز + بانر + إشعار المتصفح (بعد الإذن)
- SignalR لحظي

### 7.2 ما يحتاجه إشعار والصفحة مغلقة بالكامل؟
- مشروع **Firebase** (خطة Spark المجانية كافية)

### 7.3 خطوات Firebase (مرة واحدة)

1. ادخل: https://console.firebase.google.com  
2. أنشئ مشروعاً جديداً.  
3. أضف تطبيق **Web** وانسخ الإعدادات (`apiKey`, `projectId`, …).  
4. Project settings → **Cloud Messaging** → Web Push certificates → Generate key pair (مفتاح VAPID العام).  
5. Project settings → **Service accounts** → Generate new private key  
   احفظ الملف باسم:
   ```text
   publish-win\firebase-service-account.json
   ```
   **لا ترفع هذا الملف للعامة.**

6. عدّل `publish-win\wwwroot\firebase-config.js` (انظر المثال `firebase-config.js.example`):
   ```js
   window.QUEUE_FIREBASE = {
     enabled: true,
     vapidKey: "مفتاح_VAPID_العام",
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

7. في `appsettings.json`:
   ```json
   "Firebase": {
     "Enabled": true,
     "ProjectId": "your-project-id",
     "ServiceAccountJsonPath": "firebase-service-account.json"
   }
   ```

8. أعد تشغيل البرنامج.

9. من هاتف العميل: افتح صفحة المتابعة → اضغط **تفعيل التنبيهات** → وافق.  
   على **iPhone**: أضف الصفحة إلى الشاشة الرئيسية ثم افتحها من الأيقونة.

### 7.4 متى تصل الإشعارات؟
| الحدث | تنبيه |
|--------|--------|
| متبقي رقمان / رقم واحد | نعم (NEAR) |
| استدعاء الرقم | نعم |
| تخطي مؤقت | نعم |
| انتهاء مهلة التخطي | نعم |
| تحويل للمدير | نعم |
| انتهاء الخدمة | رسالة + زر واتساب |

---

## 8) قائمة تحقق يوم العمل

- [ ] `JwtSecret` مُغيَّر
- [ ] `QueueSystem.exe` يعمل
- [ ] ظهر رابط النفق (أو النطاق الثابت)
- [ ] `/api/health` يرجع `status: ok`
- [ ] تسجيل تجريبي من `/form/`
- [ ] استدعاء من `/employee/`
- [ ] صفحة المتابعة تعرض الشباكين والمتبقي
- [ ] (اختياري) Firebase مفعّل وإذن الإشعارات ممنوح

---

## 9) أعطال شائعة وحلولها

| المشكلة | الحل |
|---------|------|
| لم يظهر رابط النفق | تأكد من وجود `cloudflared.exe` بجانب exe والإنترنت يعمل |
| العملاء لا يفتحون الرابط | انسخ الرابط الجديد بعد إعادة التشغيل (وضع quick) |
| «لا توجد تسجيلات» عند الباحث | تأكد أنك حدّثت المشروع كاملاً وليس HTML فقط، وأعد التشغيل |
| فشل الدخول | بعد 5 محاولات خاطئة يُقفل الحساب مؤقتاً — انتظر أو راجع قاعدة البيانات |
| لا إشعارات والصفحة مغلقة | Firebase غير مفعّل أو لم يُمنح إذن الإشعارات |
| Windows يمنع التشغيل | Properties → Unblock إن وُجد، أو SmartScreen → Run anyway |

---

## 10) ملخص مسار «من المضغوط إلى الرابط»

```text
1. فك الضغط → C:\QueueSystem
2. publish.ps1 → publish-win\QueueSystem.exe
3. غيّر JwtSecret
4. ضع cloudflared.exe بجانب exe
5. شغّل QueueSystem.exe
6. انسخ الرابط العام = رابط البوابة
7. (اختياري) Named Tunnel + Firebase
8. افتح /manager وغيّر كلمة مرور admin
```

---

**نهاية دليل التشغيل مع النفق**
