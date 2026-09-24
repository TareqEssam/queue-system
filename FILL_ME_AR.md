# بيانات يجب تعبئتها قبل الإنتاج على MonsterASP

هذه القائمة تخص نشر QueueSystem كـ ASP.NET Core على MonsterASP، ولا تتطلب Cloudflare Tunnel عندما يكون الموقع منشوراً مباشرةً عبر HTTPS.

## 1) بيانات الموقع والإدارة

في `appsettings.json` أدخل القيم الحقيقية لـ:

- `PublicBaseUrl` (يفضل عنوان HTTPS النهائي للموقع).
- `Administration:NameAr` و`Administration:NameEn`.
- `Administration:LocationTextAr` و`Administration:LocationTextEn`.
- `Administration:LocationImageUrl` للصورة الحقيقية لموقع الإدارة.
- `Queue:WhatsAppAdmin` إذا كان الرقم مختلفاً عن القيمة الحالية.

لا يتم اختراع أي قيمة مكانية داخل الحزمة.

## 2) حساب المدير

في أول تشغيل لقاعدة بيانات جديدة يجب توفير كلمة مرور قوية من خلال:

`QUEUE_ADMIN_PASSWORD`

أو `Security:InitialAdminPassword`.

لا توجد كلمة مرور مدير افتراضية مقبولة. النظام يرفض الكلمات القصيرة من 10 أحرف وكذلك كلمة المرور التاريخية `Admin@12345`.

## 3) Firebase Web Push

أكمل:

- `wwwroot/firebase-config.js` بالـ Web App config الحقيقي.
- Public VAPID key في `vapidKey`.
- `Firebase:Enabled = true`.
- `Firebase:ProjectId`.
- ملف Service Account الحقيقي في مكان آمن على السيرفر، وليس داخل GitHub.

التسجيل في Web SDK يستخدم Firebase Installation ID (FID).

## 4) HTTPS والهاتف

يجب أن يكون الرابط النهائي HTTPS. على iPhone/iPad يجب إضافة الموقع إلى الشاشة الرئيسية وفتحه من الأيقونة لتشغيل Web Push. على Android يجب السماح بإشعارات الموقع من المتصفح.

## 5) الاختبار قبل العملاء الحقيقيين

راجع `docs/PRODUCTION_CHECKLIST_AR.md` ونفذ اختباراً حقيقياً على رابط MonsterASP قبل التشغيل.
