using Microsoft.EntityFrameworkCore;
using QueueSystem.Core;
using QueueSystem.Data;

namespace QueueSystem.Services;

public class TicketQueryService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public TicketQueryService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<object?> GetPublicStatusAsync(int clientNumber, string rawToken, string? language = null, CancellationToken ct = default)
    {
        if (clientNumber <= 0 || string.IsNullOrWhiteSpace(rawToken))
            return null;

        var hash = Sha256Hex(rawToken.Trim());
        var ticket = await _db.Tickets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.ClientNumber == clientNumber && t.TokenHash == hash, ct);

        if (ticket == null)
            return null;

        var desks = await _db.DeskStates.AsNoTracking().ToListAsync(ct);
        var maxDesk = desks.Count > 0 ? desks.Max(d => d.CurrentNumber) : 0;
        var d11 = desks.FirstOrDefault(d => d.Desk == 11)?.CurrentNumber ?? 0;
        var d12 = desks.FirstOrDefault(d => d.Desk == 12)?.CurrentNumber ?? 0;

        var state = await _db.SystemStates.AsNoTracking().FirstOrDefaultAsync(ct);
        var queueSequence = state?.QueueSequence ?? 0;

        var remaining = 0;
        if (ticket.Status == TicketStatuses.Waiting || ticket.Status == TicketStatuses.Skipped)
            remaining = Math.Max(0, ticket.ClientNumber - maxDesk);

        // مهلة التخطي: كم استدعاء متبقٍ قبل انتهاء الفرصة
        int? skipCallsLeft = null;
        if (ticket.Status == TicketStatuses.Skipped && ticket.SkipExpireAtSequence.HasValue)
            skipCallsLeft = (int)Math.Max(0, ticket.SkipExpireAtSequence.Value - queueSequence);

        var lang = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        var adminName = _config["Administration:NameAr"] ?? "الإدارة العامة للخدمات الحكومية";
        var adminNameEn = _config["Administration:NameEn"] ?? "Government Services Administration";
        var locationAr = _config["Administration:LocationTextAr"] ?? "يرجى التوجه إلى مكتب الإدارة المحدد من موظف الشباك.";
        var locationEn = _config["Administration:LocationTextEn"] ?? "Please go to the administration office specified by the service desk employee.";
        var locationImageUrl = string.IsNullOrWhiteSpace(_config["Administration:LocationImageUrl"]) ? null : _config["Administration:LocationImageUrl"]!.Trim();
        var whatsappAdmin = _config["Queue:WhatsAppAdmin"] ?? "201154202333";
        var whatsappUrl = whatsappAdmin.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? whatsappAdmin
            : "https://wa.me/" + new string(whatsappAdmin.Where(char.IsDigit).ToArray());

        string? endMessage = null;
        bool missed = false;
        if (ticket.Status == TicketStatuses.Closed)
        {
            var served = ticket.Desk.HasValue || ticket.CalledAt.HasValue;
            missed = !served;
            if (served)
            {
                endMessage = lang == "en"
                    ? $"{adminNameEn} is always pleased to serve you.\nYour service at the desk has been completed.\n\nYou can follow the execution status of your request by typing \"requests\".\nFor the administration services menu, type «100»."
                    : $"{adminName} يسعدها دائماً تلبية طلباتكم وخدمتكم.\nتم الانتهاء من خدمتكم في الشباك.\n\nيمكنكم متابعة الموقف التنفيذي لطلبكم من خلال كتابة كلمة «طلبات».\nولمعرفة خدمات الإدارة اكتبوا «100».";
            }
            else
            {
                endMessage = lang == "en"
                    ? "Your turn at the administration desk has expired.\nThe current number has passed your ticket.\nPlease take a new ticket from the queue machine to register again.\nThank you for your understanding."
                    : $"فات دوركم على شباك {adminName}.\nالرقم الحالي على الشباك قد تجاوز رقم تذكرتكم.\nيُرجى سحب رقم جديد من ماكينة الأرقام لإعادة التسجيل.\nنشكركم على تفهمكم.";
            }
        }
        else if (ticket.Status == TicketStatuses.Transferred)
        {
            endMessage = lang == "en"
                ? $"Your request has been transferred to {adminNameEn}.\n{locationEn}\n\nYou can follow the status of your request by typing \"requests\"."
                : $"تم تحويل طلبكم إلى {adminName}.\n{locationAr}\n\nيمكنكم متابعة الموقف التنفيذي لطلبكم من خلال كتابة كلمة «طلبات».";
        }
        else if (ticket.Status == TicketStatuses.SkippedExpired)
        {
            endMessage = lang == "en"
                ? "The grace period after your call has ended because you did not attend.\nPlease take a new ticket from the queue machine to register again.\nThank you for your understanding."
                : "انتهت المهلة الممنوحة لعدم حضوركم عند استدعاء رقمكم.\nيُرجى سحب رقم جديد من ماكينة الأرقام لإعادة التسجيل في النظام.\nنشكركم على تفهمكم.";
        }
        string? skipWarning = null;
        if (ticket.Status == TicketStatuses.Skipped)
        {
            skipWarning = lang == "en"
                ? "⚠️ Important notice\nYour number was called but you did not attend.\nYou have a grace period until 5 subsequent numbers are called.\nAfter that, your turn will be cancelled and you must take a new ticket."
                : "⚠️ تنبيه شديد الأهمية\nلم يتم حضوركم عند استدعاء رقمكم من شباك الإدارة.\nلديكم مهلة حتى يتم استدعاء 5 أرقام تالية لرقمكم.\nفي حال عدم الحضور خلال هذه المهلة سيتم إلغاء دوركم وسيتوجب عليكم سحب رقم جديد من ماكينة الأرقام.";
        }

        return new
        {
            success = true,
            clientNumber = ticket.ClientNumber,
            status = ticket.Status,
            desk = ticket.Desk,
            calledAt = ticket.CalledAt,
            skippedAt = ticket.SkippedAt,
            skipExpireAtSequence = ticket.SkipExpireAtSequence,
            skipCallsLeft,
            skipWarning,
            transferMessage = ticket.TransferMessage,
            liveEventSeq = ticket.LiveEventSeq,
            maxDeskCurrent = maxDesk,
            d11,
            d12,
            queueSequence,
            remaining,
            isTerminal = TicketStatuses.IsTerminal(ticket.Status),
            endMessage,
            whatsappUrl,
            administration = new
            {
                name = lang == "en" ? adminNameEn : adminName,
                locationText = lang == "en" ? locationEn : locationAr,
                locationImageUrl
            },
            missed
        };
    }

    public async Task<(bool Success, string Message)> RegisterPushAsync(
        int clientNumber,
        string rawToken,
        string fcmToken,
        string? language = null,
        string? pushType = null,
        string? endpoint = null,
        string? p256dh = null,
        string? auth = null,
        CancellationToken ct = default)
    {
        var lang = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        var type = string.Equals(pushType, "webpush", StringComparison.OrdinalIgnoreCase) ? "webpush" : "fcm";

        if (type == "webpush")
        {
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
                return (false, "تعذر الحصول على بيانات إشعارات الجهاز. تأكد من السماح بالإشعارات ثم حاول مرة أخرى.");

            if (endpoint.Length > 2048 || p256dh.Length > 256 || auth.Length > 256)
                return (false, "بيانات الإشعار غير صالحة.");

            // Endpoint hash is the stable local key; endpoint itself is stored encrypted by the
            // browser-level transport and is required by the Web Push protocol.
            fcmToken = Sha256Hex(endpoint.Trim());
        }
        else
        {
            // Firebase 12+ uses Firebase Installation ID (FID), not the obsolete registration token.
            if (string.IsNullOrWhiteSpace(fcmToken) || fcmToken.Length > 512 || fcmToken == "browser-local" || fcmToken.Length <= 20)
                return (false, "تعذر الحصول على معرّف الإشعارات من Firebase. تأكد من HTTPS وإعدادات الإشعارات ثم حاول مرة أخرى.");
        }

        var hash = Sha256Hex(rawToken.Trim());
        var ticket = await _db.Tickets
            .FirstOrDefaultAsync(t => t.ClientNumber == clientNumber && t.TokenHash == hash, ct);

        if (ticket == null)
            return (false, "التذكرة غير موجودة.");

        if (TicketStatuses.IsTerminal(ticket.Status))
            return (false, "التذكرة منتهية.");

        // حد أقصى 4 أجهزة نشطة لكل تذكرة. يمكن للعميل إعادة تفعيل الجهاز نفسه دون إنشاء سجل جديد.
        var existing = await _db.PushRegistrations
            .Where(p => p.TicketId == ticket.Id)
            .OrderByDescending(p => p.LastSeenAt ?? p.CreatedAt)
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(p => p.PushType == type && p.FcmToken == fcmToken);
        if (match != null)
        {
            match.IsActive = true;
            match.LastSeenAt = DateTime.UtcNow;
            match.Language = lang;
            if (type == "webpush")
            {
                match.Endpoint = endpoint;
                match.P256dh = p256dh;
                match.Auth = auth;
            }
            await _db.SaveChangesAsync(ct);
            return (true, "تم تحديث التسجيل.");
        }

        if (existing.Count(p => p.IsActive) >= 4)
        {
            var oldest = existing.Where(p => p.IsActive).OrderBy(p => p.LastSeenAt ?? p.CreatedAt).First();
            oldest.IsActive = false;
        }

        _db.PushRegistrations.Add(new Data.Entities.PushRegistration
        {
            TicketId = ticket.Id,
            FcmToken = fcmToken,
            PushType = type,
            Endpoint = type == "webpush" ? endpoint : null,
            P256dh = type == "webpush" ? p256dh : null,
            Auth = type == "webpush" ? auth : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            Language = lang
        });

        await _db.SaveChangesAsync(ct);
        return (true, "تم تسجيل التنبيهات.");
    }

    public async Task<bool> UnregisterPushAsync(
        int clientNumber,
        string rawToken,
        string fcmToken,
        string? pushType = null,
        string? endpoint = null,
        CancellationToken ct = default)
    {
        if (clientNumber <= 0 || string.IsNullOrWhiteSpace(rawToken))
            return false;

        var type = string.Equals(pushType, "webpush", StringComparison.OrdinalIgnoreCase) ? "webpush" : "fcm";
        var key = type == "webpush" && !string.IsNullOrWhiteSpace(endpoint)
            ? Sha256Hex(endpoint.Trim())
            : fcmToken;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var hash = Sha256Hex(rawToken.Trim());
        var ticket = await _db.Tickets.FirstOrDefaultAsync(
            t => t.ClientNumber == clientNumber && t.TokenHash == hash, ct);
        if (ticket == null)
            return false;

        var changed = await _db.PushRegistrations
            .Where(p => p.TicketId == ticket.Id && p.FcmToken == key && p.PushType == type && p.IsActive)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.IsActive, false)
                .SetProperty(p => p.LastSeenAt, DateTime.UtcNow), ct);

        return changed > 0;
    }

    private static string Sha256Hex(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
