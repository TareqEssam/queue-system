# إصلاحات الإنتاج المطبّقة

## التزامن
- `QueueLock` عام (Singleton) لكل عمليات الطابور والتسجيل
- لا يُمنع تكرار رقم التذكرة (سياسة متعمدة)

## منطق R6
- NEXT بدون تذكرة: يتقدم مؤشر الشباك والتسلسل
- مهلة التخطي (5): تُفحص **بعد** زيادة Sequence

## SignalR والأمان
- `QueueHub` للموظفين مع `[Authorize]`
- `PublicQueueHub` للعملاء فقط
- لا بث `Clients.All` ببيانات شركات
- الباحث والمدير: تحديث لحظي عبر SignalR + polling احتياطي عند الانقطاع فقط
- العميل: نفس الأسلوب

## SQLite
- `PRAGMA journal_mode=WAL`
- `busy_timeout=5000`
- `synchronous=NORMAL`

## CORS
- للتطوير: مفتوح إن كانت `Cors:AllowedOrigins` فارغة
- للإنتاج: ضع النطاقات صراحة في `appsettings.json`:
  ```json
  "Cors": {
    "AllowedOrigins": [ "https://queue.your-domain.com" ]
  }
  ```

## الأسرار
- رفض الإقلاع إن `JwtSecret` ضعيف/افتراضي
- كلمة مرور المدير من `QUEUE_ADMIN_PASSWORD` أو `Security:InitialAdminPassword`

## النفق
- الإنتاج: `Tunnel:Mode = named` فقط
- `quick` للتجربة فقط

## FCM
- يُفعَّل يدوياً عبر Firebase + `firebase-config.js` + Service Account
- راجع الدليل `01_دليل_التشغيل_مع_النفق.md` القسم 7
