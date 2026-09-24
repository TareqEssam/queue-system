# تحصين الإنتاج قبل التعريض للعامة

## 1) كلمة مرور المدير
- **لا يوجد** كلمة مرور افتراضية `[لا توجد كلمة مرور افتراضية — استخدم QUEUE_ADMIN_PASSWORD]` بعد الآن.
- عيّن قبل أول تشغيل:
  - متغير بيئة: `QUEUE_ADMIN_PASSWORD=كلمة_قوية_طويلة`
  - أو في appsettings: `Security:InitialAdminPassword`
- إذا كان النفق مفعّلاً وحساب admin ما زال بالكلمة الافتراضية القديمة → **يرفض البرنامج الإقلاع**.

## 2) JwtSecret
- لا تستخدم قيماً مثل `dev-only-...` أو `CHANGE_THIS`.
- إن تُرك فارغاً/ضعيفاً → يُولَّد سراً عشوائياً في `data/jwt.secret` تلقائياً.
- احفظ هذا الملف مع النسخ الاحتياطي ولا تشاركه.

## 3) CORS
- مع `Tunnel:Enabled=true` **يجب** تعيين:
```json
"Cors": {
  "AllowedOrigins": [ "https://queue.your-domain.com" ]
}
```
- بدون ذلك يرفض البرنامج الإقلاع.
- بدون نفق: يُسمح فقط بـ localhost.

## 4) Rate limiting
- مفعّل على `/api/public/*` (60 طلب/دقيقة لكل IP).
- مفعّل على `/api/auth/*` (15 طلب/دقيقة لكل IP).

## 5) Named Tunnel (رابط ثابت)
راجع `05_رابط_ثابت_ونسخ_احتياطي.md` — استخدم `Mode: named` وليس `quick`.

## 6) Cloudflare Access (موصى به بقوة)
طبقة مجانية قبل وصول الطلب لجهازك:

1. Cloudflare Zero Trust → Access → Applications
2. أضف تطبيقاً للنطاق `https://queue.your-domain.com/employee*`
3. وآخر لـ `/manager*`
4. سياسة: بريدك فقط (One-time PIN أو Google)
5. **لا** تضف `/form` أو `/track` حتى يبقى التسجيل والمتابعة مفتوحين للعملاء

النتيجة: حتى لو تسرّبت كلمة مرور باحث، المهاجم يحتاج أيضاً تجاوز Access على الحافة.

## 7) النسخ الاحتياطي خارج الجهاز
- النسخ المحلي في `backups/` كل 30 دقيقة يحمي من تلف الملف.
- انسخ مجلد `backups` + `data/jwt.secret` أسبوعياً إلى:
  - فلاشة
  - أو Google Drive / OneDrive الشخصي
- هذا يحميك إن سُرق الجهاز أو تعطل الهارد.

## قائمة تشغيل إنتاجية
1. عيّن QUEUE_ADMIN_PASSWORD
2. Cors:AllowedOrigins = نطاقك فقط
3. Tunnel Mode = named + DNS
4. (اختياري) Backup EncryptionPassword
5. Cloudflare Access على /employee و /manager
6. شغّل البرنامج وتأكد أن /api/health يعمل
