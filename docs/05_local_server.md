# الرابط الثابت + النسخ الاحتياطي + أرشفة نهاية اليوم

---

## أ) كيف تجعل الرابط ثابتاً (لا يتغيّر بعد إغلاق الجهاز)؟

الوضع الافتراضي `Tunnel:Mode = "quick"` يعطي رابطاً مؤقتاً مثل:

```text
https://xxxx.trycloudflare.com
```

هذا الرابط **يتغيّر** في كل إعادة تشغيل للنفق أو للجهاز.

### الحل: Named Tunnel (مرة واحدة)

#### المتطلبات
- حساب Cloudflare مجاني
- نطاق (دومين) مضبوط على Cloudflare

#### الخطوات

1. ضع `cloudflared.exe` بجانب `QueueSystem.exe`.

2. من مجلد البرنامج في PowerShell:

```powershell
.\cloudflared.exe tunnel login
```

أكمل الموافقة في المتصفح.

3. أنشئ النفق:

```powershell
.\cloudflared.exe tunnel create queue-system
```

سيُنشأ ملف اعتمادات JSON — ضعه بجانب البرنامج واحفظ اسمه.

4. أنشئ ملف `cloudflared-config.yml`:

```yaml
tunnel: queue-system
credentials-file: C:\QueueSystem\publish-win\TUNNEL-ID.json

ingress:
  - hostname: queue.your-domain.com
    service: http://127.0.0.1:5000
  - service: http_status:404
```

استبدل المسار والنطاق بقيمك الحقيقية.

5. في لوحة Cloudflare → DNS:

| Type | Name | Target | Proxy |
|------|------|--------|-------|
| CNAME | queue | TUNNEL-ID.cfargotunnel.com | Proxied ON |

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

**الرابط الثابت بعد ذلك:**

```text
https://queue.your-domain.com
```

بعد إغلاق الجهاز وفتحه في اليوم التالي — **نفس الرابط** طالما الإعدادات لم تتغير.

> بدون نطاق خاص: يبقى `quick` للتجربة فقط وسيتغيّر الرابط.

---

## ب) أرشفة نهاية اليوم (مطبّق في الكود)

عند أول عملية في يوم جديد:

1. كل التذاكر غير النهائية تُضاف إلى جدول الأرشيف `ArchiveRecords` بإجراء `DAILY_RESET`
2. ثم تُغلق حالتها إلى `CLOSED`
3. تُصفَّر الشباكات وعداد الطابور

البحث من واجهة المدير (اسم شركة / رقم سجل) يستمر على الأرشيف التاريخي.

---

## ج) النسخ الاحتياطي (مطبّق — اختياري)

في `appsettings.json`:

```json
"Backup": {
  "Enabled": true,
  "IntervalMinutes": 30,
  "Folder": "backups",
  "KeepCount": 14,
  "EncryptionPassword": ""
}
```

| الحقل | المعنى |
|--------|--------|
| Enabled | تفعيل / إيقاف |
| IntervalMinutes | كل كم دقيقة (الحد الأدنى 5) |
| Folder | مجلد النسخ |
| KeepCount | عدد آخر النسخ المحتفظ بها |
| EncryptionPassword | إن وُضعت كلمة سر → ملفات `.db.enc` مشفّرة AES |

- بدون كلمة سر: `queue_YYYYMMDD_HHmmss.db`
- مع كلمة سر: `queue_....db.enc` (والنسخة العادية تُحذف بعد التشفير)
- عند إيقاف البرنامج يُحاول أخذ نسخة أخيرة

### استعادة من نسخة غير مشفّرة
1. أوقف البرنامج
2. انسخ ملف النسخة باسم `queue.db` مكان الحالي
3. شغّل البرنامج

### استعادة من نسخة مشفّرة
فك التشفير بكلمة السر نفسها عبر `BackupService.DecryptFile` (أداة مساعدة في الكود).

**نصيحة:** انسخ مجلد `backups` يدوياً إلى قرص خارجي أو OneDrive مرة يومياً إن أمكن.

---

## د) قائمة تحقق

- [ ] Mode named + DNS CNAME → رابط ثابت
- [ ] Backup Enabled true
- [ ] (اختياري) EncryptionPassword قوية محفوظة بأمان
- [ ] ظهور مجلد backups بعد التشغيل
