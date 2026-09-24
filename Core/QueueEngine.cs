using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using QueueSystem.Data;
using QueueSystem.Data.Entities;

namespace QueueSystem.Core;

/// <summary>
/// محرك الطابور — كل تعديل على حالة الطابور يمر من خلال QueueLock لمنع سباقات NEXT/SKIP/TRANSFER.
/// التكرار في رقم العميل مدعوم عمداً: استدعاء رقم يعلن كل التذاكر النشطة التي تحمل الرقم نفسه.
/// </summary>
public class QueueEngine
{
    private readonly AppDbContext _db;
    private readonly ILogger<QueueEngine> _logger;
    private readonly QueueLock _queueLock;
    private readonly int _skipGraceCalls;
    private readonly string _adminNameAr;
    private readonly string _adminNameEn;
    private readonly string _adminLocationTextAr;
    private readonly string _adminLocationTextEn;
    private readonly string? _adminLocationImageUrl;

    public QueueEngine(
        AppDbContext db,
        ILogger<QueueEngine> logger,
        IConfiguration config,
        QueueLock queueLock)
    {
        _db = db;
        _logger = logger;
        _queueLock = queueLock;
        _skipGraceCalls = Math.Max(1, config.GetValue("Queue:SkipGraceCalls", 5));
        _adminNameAr = config["Administration:NameAr"] ?? "الإدارة العامة للخدمات الحكومية";
        _adminNameEn = config["Administration:NameEn"] ?? "Government Services Administration";
        _adminLocationTextAr = config["Administration:LocationTextAr"] ?? "يرجى التوجه إلى مكتب الإدارة المحدد من موظف الشباك.";
        _adminLocationTextEn = config["Administration:LocationTextEn"] ?? "Please go to the administration office specified by the service desk employee.";
        _adminLocationImageUrl = string.IsNullOrWhiteSpace(config["Administration:LocationImageUrl"]) ? null : config["Administration:LocationImageUrl"]!.Trim();
    }

    public async Task<QueueActionResult> CallNextAsync(Employee employee, int desk, CancellationToken ct = default)
    {
        using (await _queueLock.AcquireAsync(ct))
        {
            await EnsureDailyResetAsync(ct);

            var state = await GetSystemStateAsync(ct);
            var nextNumber = state.LastCalledNumber + 1;

            var ticket = await _db.Tickets
                .Include(t => t.Registration)
                .Where(t => t.ClientNumber == nextNumber &&
                            (t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped))
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(ct);

            if (ticket == null)
            {
                var now = DateTime.UtcNow;
                state.LastCalledNumber = nextNumber;
                state.QueueSequence++;
                state.UpdatedAt = now;

                var deskState = await _db.DeskStates.FirstOrDefaultAsync(d => d.Desk == desk, ct);
                if (deskState == null)
                {
                    deskState = new DeskState
                    {
                        Desk = desk,
                        CurrentNumber = nextNumber,
                        UpdatedAt = now,
                        UpdatedBy = employee.DisplayName
                    };
                    _db.DeskStates.Add(deskState);
                }
                else
                {
                    deskState.CurrentNumber = nextNumber;
                    deskState.UpdatedAt = now;
                    deskState.UpdatedBy = employee.DisplayName;
                }

                var notifications = await ProcessExpiredSkipsAsync(state, now, ct);
                await RecalculateWaitingCountAsync(state, ct);
                var maxDesk = await GetMaxDeskCurrentAsync(ct, nextNumber);
                notifications.AddRange(await CollectNearNotificationsAsync(state, maxDesk, ct));
                await _db.SaveChangesAsync(ct);

                return new QueueActionResult
                {
                    Success = false,
                    StateChanged = true,
                    Message = $"لا يوجد عميل برقم {nextNumber} في الانتظار. تم تقديم مؤشر الشباك.",
                    ClientNumber = nextNumber,
                    Desk = desk,
                    MaxDeskCurrent = maxDesk,
                    LiveEventSeq = state.LiveEventSeq,
                    Notifications = notifications
                };
            }

            return await ExecuteCallAsync(ticket, desk, employee, state, isManual: false, ct);
        }
    }

    public async Task<QueueActionResult> CallSpecificAsync(Employee employee, int desk, int clientNumber, CancellationToken ct = default)
    {
        using (await _queueLock.AcquireAsync(ct))
        {
            await EnsureDailyResetAsync(ct);

            var ticket = await _db.Tickets
                .Include(t => t.Registration)
                .Where(t => t.ClientNumber == clientNumber &&
                            (t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped))
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(ct);

            if (ticket == null)
                return new QueueActionResult { Success = false, Message = $"لا توجد تذكرة نشطة للرقم {clientNumber}." };

            var state = await GetSystemStateAsync(ct);
            return await ExecuteCallAsync(ticket, desk, employee, state, isManual: true, ct);
        }
    }

    public async Task<QueueActionResult> SkipCurrentAsync(Employee employee, int desk, CancellationToken ct = default)
    {
        using (await _queueLock.AcquireAsync(ct))
        {
            await EnsureDailyResetAsync(ct);

            var deskState = await _db.DeskStates.FirstOrDefaultAsync(d => d.Desk == desk, ct);
            if (deskState == null || deskState.CurrentNumber <= 0)
                return new QueueActionResult { Success = false, Message = "لا يوجد رقم حالي على هذا الشباك." };

            var clientNumber = deskState.CurrentNumber;
            var calledTickets = await _db.Tickets
                .Include(t => t.Registration)
                .Where(t => t.ClientNumber == clientNumber && t.Status == TicketStatuses.Called)
                .OrderBy(t => t.Id)
                .ToListAsync(ct);

            if (calledTickets.Count == 0)
                return new QueueActionResult { Success = false, Message = "التذاكر الحالية غير موجودة أو ليست في حالة استدعاء." };

            var state = await GetSystemStateAsync(ct);
            var now = DateTime.UtcNow;
            var eventSeq = ++state.LiveEventSeq;
            var expireAt = state.QueueSequence + _skipGraceCalls;
            var matches = new List<DuplicateMatchInfo>();
            var notifications = new List<TicketNotificationInfo>();

            foreach (var ticket in calledTickets)
            {
                ticket.Status = TicketStatuses.Skipped;
                ticket.SkippedAt = now;
                ticket.SkipExpireAtSequence = expireAt;
                ticket.LiveEventSeq = eventSeq;
                await AddArchiveAsync(ticket, desk, employee.DisplayName, "SKIP", TicketStatuses.Skipped, null, now);

                var reg = ticket.Registration;
                matches.Add(new DuplicateMatchInfo
                {
                    TicketId = ticket.Id,
                    RegistrationId = ticket.RegistrationId,
                    TokenHash = ticket.TokenHash,
                    CompanyName = reg?.CompanyName ?? "",
                    RegNumber = reg?.CommercialRegister ?? "",
                    Source = reg?.Source ?? "",
                    CreatedAt = reg?.CreatedAt ?? ticket.CreatedAt
                });
                notifications.Add(new TicketNotificationInfo
                {
                    TicketId = ticket.Id,
                    TokenHash = ticket.TokenHash,
                    ClientNumber = ticket.ClientNumber,
                    Status = TicketStatuses.Skipped,
                    Desk = desk,
                    LiveEventSeq = ticket.LiveEventSeq
                });
            }

            deskState.CurrentNumber = 0;
            deskState.UpdatedAt = now;
            deskState.UpdatedBy = employee.DisplayName;

            _db.QueueLogs.Add(new QueueLog
            {
                Action = "SKIP",
                ClientNumber = clientNumber,
                Desk = desk,
                EmployeeDisplayName = employee.DisplayName,
                Details = $"تخطي — مهلة حتى Sequence {expireAt}" + (calledTickets.Count > 1 ? $" — {calledTickets.Count} تذاكر" : ""),
                Timestamp = now
            });

            await RecalculateWaitingCountAsync(state, ct);
            state.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            var maxDesk = await GetMaxDeskCurrentAsync(ct, 0);
            return new QueueActionResult
            {
                Success = true,
                StateChanged = true,
                Message = $"تم تخطي الرقم {clientNumber}. المهلة: {_skipGraceCalls} استدعاءات." +
                          (calledTickets.Count > 1 ? $" ({calledTickets.Count} تسجيلات)" : ""),
                ClientNumber = clientNumber,
                Desk = desk,
                TicketId = calledTickets[^1].Id,
                DuplicateCount = calledTickets.Count,
                Matches = matches,
                LiveEventSeq = eventSeq,
                Status = TicketStatuses.Skipped,
                MaxDeskCurrent = maxDesk,
                Notifications = notifications
            };
        }
    }

    public async Task<QueueActionResult> TransferToManagerAsync(
        Employee employee,
        int registrationId,
        string? note,
        CancellationToken ct = default)
    {
        using (await _queueLock.AcquireAsync(ct))
        {
            var reg = await _db.Registrations.FindAsync(new object[] { registrationId }, ct);
            if (reg == null)
                return new QueueActionResult { Success = false, Message = "التسجيل غير موجود." };

            var ticket = await _db.Tickets
                .Where(t => t.RegistrationId == registrationId && !TicketStatuses.IsTerminal(t.Status))
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(ct);

            if (ticket == null)
                return new QueueActionResult { Success = false, Message = "لا توجد تذكرة نشطة لهذا التسجيل." };

            var state = await GetSystemStateAsync(ct);
            var now = DateTime.UtcNow;

            ticket.Status = TicketStatuses.Transferred;
            ticket.TransferredAt = now;
            ticket.TransferredBy = employee.DisplayName;
            ticket.TransferNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            ticket.TransferMessage = BuildTransferMessageAr();
            ticket.LiveEventSeq = ++state.LiveEventSeq;

            if (ticket.Desk is int ticketDesk)
            {
                var anotherCalledTicket = await _db.Tickets.AnyAsync(t =>
                    t.Id != ticket.Id &&
                    t.ClientNumber == ticket.ClientNumber &&
                    t.Status == TicketStatuses.Called &&
                    t.Desk == ticketDesk, ct);

                var deskState = await _db.DeskStates.FirstOrDefaultAsync(d => d.Desk == ticketDesk, ct);
                if (!anotherCalledTicket && deskState != null && deskState.CurrentNumber == ticket.ClientNumber)
                {
                    deskState.CurrentNumber = 0;
                    deskState.UpdatedAt = now;
                    deskState.UpdatedBy = employee.DisplayName;
                }
            }

            _db.QueueLogs.Add(new QueueLog
            {
                Action = "TRANSFER",
                ClientNumber = ticket.ClientNumber,
                Desk = ticket.Desk,
                EmployeeDisplayName = employee.DisplayName,
                Details = ticket.TransferNote,
                Timestamp = now
            });

            await AddArchiveAsync(ticket, ticket.Desk, employee.DisplayName, "TRANSFER", TicketStatuses.Transferred, ticket.TransferNote, now);
            await RecalculateWaitingCountAsync(state, ct);
            state.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            return new QueueActionResult
            {
                Success = true,
                StateChanged = true,
                Message = $"تم تحويل الرقم {ticket.ClientNumber} إلى المدير.",
                ClientNumber = ticket.ClientNumber,
                Desk = ticket.Desk,
                TicketId = ticket.Id,
                LiveEventSeq = ticket.LiveEventSeq,
                Status = TicketStatuses.Transferred,
                TransferMessage = ticket.TransferMessage,
                CompanyName = reg.CompanyName,
                RegNumber = reg.CommercialRegister,
                RegistrationId = reg.Id,
                MaxDeskCurrent = await GetMaxDeskCurrentAsync(ct, 0),
                Notifications = new List<TicketNotificationInfo>
                {
                    new()
                    {
                        TicketId = ticket.Id,
                        TokenHash = ticket.TokenHash,
                        ClientNumber = ticket.ClientNumber,
                        Status = TicketStatuses.Transferred,
                        Desk = ticket.Desk,
                        LiveEventSeq = ticket.LiveEventSeq
                    }
                }
            };
        }
    }

    private async Task<QueueActionResult> ExecuteCallAsync(
        Ticket ticket,
        int desk,
        Employee employee,
        SystemState state,
        bool isManual,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var clientNumber = ticket.ClientNumber;
        var notifications = new List<TicketNotificationInfo>();

        // عند استدعاء رقم جديد على نفس الشباك: الخدمة السابقة تنتهي فوراً.
        var previous = await _db.Tickets
            .Where(t => t.Desk == desk && t.Status == TicketStatuses.Called && t.ClientNumber != clientNumber)
            .ToListAsync(ct);

        foreach (var prev in previous)
        {
            prev.Status = TicketStatuses.Closed;
            prev.LiveEventSeq = ++state.LiveEventSeq;
            await AddArchiveAsync(prev, desk, employee.DisplayName, "CLOSE", TicketStatuses.Closed, "إغلاق تلقائي عند استدعاء رقم جديد", now);
            notifications.Add(new TicketNotificationInfo
            {
                TicketId = prev.Id,
                TokenHash = prev.TokenHash,
                ClientNumber = prev.ClientNumber,
                Status = TicketStatuses.Closed,
                Desk = desk,
                LiveEventSeq = prev.LiveEventSeq,
                WasServed = true
            });
        }

        // أي رقم أقل من الرقم المستدعى ولم يعد له فرصة يجب أن ينتهي فوراً.
        notifications.AddRange(await ClosePassedTicketsAsync(clientNumber, state, now, employee.DisplayName, ct));

        // استدعاء كل التذاكر النشطة التي تحمل الرقم نفسه: لا نفترض أي تسجيل على أنه الصحيح.
        var allMatches = await _db.Tickets
            .Include(t => t.Registration)
            .Where(t => t.ClientNumber == clientNumber &&
                        (t.Status == TicketStatuses.Waiting ||
                         t.Status == TicketStatuses.Skipped ||
                         t.Status == TicketStatuses.Called))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        if (allMatches.Count == 0)
            allMatches = new List<Ticket> { ticket };

        var eventSeq = ++state.LiveEventSeq;
        foreach (var tkt in allMatches)
        {
            tkt.Status = TicketStatuses.Called;
            tkt.Desk = desk;
            tkt.CalledAt = now;
            tkt.LiveEventSeq = eventSeq;
            tkt.SkippedAt = null;
            tkt.SkipExpireAtSequence = null;

            await AddArchiveAsync(tkt, desk, employee.DisplayName, isManual ? "MANUAL" : "NEXT", TicketStatuses.Called, null, now);

            notifications.Add(new TicketNotificationInfo
            {
                TicketId = tkt.Id,
                TokenHash = tkt.TokenHash,
                ClientNumber = tkt.ClientNumber,
                Status = TicketStatuses.Called,
                Desk = desk,
                LiveEventSeq = tkt.LiveEventSeq
            });
        }

        var deskState = await _db.DeskStates.FirstOrDefaultAsync(d => d.Desk == desk, ct);
        if (deskState == null)
        {
            deskState = new DeskState { Desk = desk };
            _db.DeskStates.Add(deskState);
        }
        deskState.CurrentNumber = clientNumber;
        deskState.UpdatedAt = now;
        deskState.UpdatedBy = employee.DisplayName;

        state.QueueSequence++;
        if (clientNumber > state.LastCalledNumber)
            state.LastCalledNumber = clientNumber;

        notifications.AddRange(await ProcessExpiredSkipsAsync(state, now, ct));
        await RecalculateWaitingCountAsync(state, ct);
        state.UpdatedAt = now;

        var maxDesk = await GetMaxDeskCurrentAsync(ct, clientNumber);
        notifications.AddRange(await CollectNearNotificationsAsync(state, maxDesk, ct));
        await _db.SaveChangesAsync(ct);

        var matchInfos = allMatches.Select(tkt =>
        {
            var reg = tkt.Registration;
            return new DuplicateMatchInfo
            {
                TicketId = tkt.Id,
                RegistrationId = tkt.RegistrationId,
                TokenHash = tkt.TokenHash,
                CompanyName = reg?.CompanyName ?? "",
                RegNumber = reg?.CommercialRegister ?? "",
                Source = reg?.Source ?? "",
                CreatedAt = reg?.CreatedAt ?? tkt.CreatedAt
            };
        }).ToList();

        var primary = matchInfos.LastOrDefault() ?? matchInfos.First();
        var msg = allMatches.Count > 1
            ? $"تم استدعاء الرقم {clientNumber} على الشباك {desk} — يشمل {allMatches.Count} تسجيلات مكررة."
            : $"تم استدعاء الرقم {clientNumber} على الشباك {desk}.";

        return new QueueActionResult
        {
            Success = true,
            StateChanged = true,
            Message = msg,
            ClientNumber = clientNumber,
            Desk = desk,
            TicketId = primary.TicketId,
            LiveEventSeq = eventSeq,
            Status = TicketStatuses.Called,
            MaxDeskCurrent = maxDesk,
            CompanyName = primary.CompanyName,
            RegNumber = primary.RegNumber,
            RegistrationId = primary.RegistrationId,
            DuplicateCount = allMatches.Count,
            Matches = matchInfos,
            Notifications = notifications
        };
    }

    private async Task<List<TicketNotificationInfo>> ProcessExpiredSkipsAsync(
        SystemState state,
        DateTime now,
        CancellationToken ct)
    {
        var notifications = new List<TicketNotificationInfo>();
        var expired = await _db.Tickets
            .Where(t => t.Status == TicketStatuses.Skipped &&
                        t.SkipExpireAtSequence != null &&
                        t.SkipExpireAtSequence <= state.QueueSequence)
            .ToListAsync(ct);

        foreach (var t in expired)
        {
            t.Status = TicketStatuses.SkippedExpired;
            t.LiveEventSeq = ++state.LiveEventSeq;
            await AddArchiveAsync(t, t.Desk, null, "SKIP_EXPIRED", TicketStatuses.SkippedExpired, null, now);
            notifications.Add(new TicketNotificationInfo
            {
                TicketId = t.Id,
                TokenHash = t.TokenHash,
                ClientNumber = t.ClientNumber,
                Status = TicketStatuses.SkippedExpired,
                Desk = t.Desk,
                LiveEventSeq = t.LiveEventSeq
            });
        }

        return notifications;
    }

    private async Task<List<TicketNotificationInfo>> ClosePassedTicketsAsync(
        int calledNumber,
        SystemState state,
        DateTime now,
        string employeeName,
        CancellationToken ct)
    {
        var notifications = new List<TicketNotificationInfo>();
        var passed = await _db.Tickets
            .Where(t => t.ClientNumber < calledNumber && t.Status == TicketStatuses.Waiting)
            .ToListAsync(ct);

        foreach (var t in passed)
        {
            t.Status = TicketStatuses.Closed;
            t.LiveEventSeq = ++state.LiveEventSeq;
            await AddArchiveAsync(t, null, employeeName, "MISSED", TicketStatuses.Closed, "فات الدور", now);
            notifications.Add(new TicketNotificationInfo
            {
                TicketId = t.Id,
                TokenHash = t.TokenHash,
                ClientNumber = t.ClientNumber,
                Status = TicketStatuses.Closed,
                Desk = null,
                LiveEventSeq = t.LiveEventSeq,
                WasServed = false
            });
        }

        return notifications;
    }

    private async Task<List<TicketNotificationInfo>> CollectNearNotificationsAsync(
        SystemState state,
        int maxDeskCurrent,
        CancellationToken ct)
    {
        var result = new List<TicketNotificationInfo>();
        if (maxDeskCurrent <= 0)
            return result;

        var waiting = await _db.Tickets
            .Where(t => (t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped))
            .Where(t => t.ClientNumber - maxDeskCurrent == 1 || t.ClientNumber - maxDeskCurrent == 2)
            .OrderBy(t => t.ClientNumber).ThenBy(t => t.Id)
            .ToListAsync(ct);

        foreach (var ticket in waiting)
        {
            var remaining = ticket.ClientNumber - maxDeskCurrent;
            if (ticket.LastNearRemainingNotified == remaining)
                continue;

            ticket.LastNearRemainingNotified = remaining;
            result.Add(new TicketNotificationInfo
            {
                TicketId = ticket.Id,
                TokenHash = ticket.TokenHash,
                ClientNumber = ticket.ClientNumber,
                Status = "NEAR",
                LiveEventSeq = ticket.LiveEventSeq,
                NearRemaining = remaining,
                MaxDeskCurrent = maxDeskCurrent
            });
        }

        return result;
    }

    private async Task<int> GetMaxDeskCurrentAsync(CancellationToken ct, int fallback)
    {
        var max = await _db.DeskStates.MaxAsync(d => (int?)d.CurrentNumber, ct);
        return max ?? fallback;
    }

    private async Task RecalculateWaitingCountAsync(SystemState state, CancellationToken ct)
    {
        state.WaitingCount = await _db.Tickets
            .CountAsync(t => t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped, ct);
    }

    private async Task AddArchiveAsync(
        Ticket ticket,
        int? desk,
        string? employeeDisplayName,
        string action,
        string statusFinal,
        string? notes,
        DateTime timestamp)
    {
        var company = ticket.Registration?.CompanyName ?? "";
        var cr = ticket.Registration?.CommercialRegister ?? "";

        if (ticket.RegistrationId.HasValue && string.IsNullOrEmpty(company))
        {
            var reg = await _db.Registrations.FindAsync(ticket.RegistrationId.Value);
            if (reg != null)
            {
                company = reg.CompanyName;
                cr = reg.CommercialRegister;
            }
        }

        _db.ArchiveRecords.Add(new ArchiveRecord
        {
            ClientNumber = ticket.ClientNumber,
            CompanyName = company,
            CommercialRegister = cr,
            Desk = desk ?? ticket.Desk,
            EmployeeDisplayName = employeeDisplayName,
            Action = action,
            StatusFinal = statusFinal,
            Notes = notes,
            Timestamp = timestamp
        });
    }

    private async Task DeactivatePushAsync(int ticketId, CancellationToken ct)
    {
        await _db.PushRegistrations
            .Where(p => p.TicketId == ticketId && p.IsActive)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IsActive, false), ct);
    }

    private string BuildTransferMessageAr()
        => $"تم تحويل طلبك إلى {(_adminNameAr)}.\n{_adminLocationTextAr}";

    private async Task EnsureDailyResetAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var state = await GetSystemStateAsync(ct);
        if (state.ActiveDayKey == today)
            return;

        _logger.LogInformation("إعادة تعيين يومية: {Old} → {New}", state.ActiveDayKey, today);

        var active = await _db.Tickets
            .Include(x => x.Registration)
            .Where(t => !TicketStatuses.IsTerminal(t.Status))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var t in active)
        {
            await AddArchiveAsync(t, t.Desk, null, "DAILY_RESET", TicketStatuses.Closed,
                $"إغلاق نهاية اليوم — الحالة السابقة: {t.Status}", now);
            t.Status = TicketStatuses.Closed;
            t.LiveEventSeq = ++state.LiveEventSeq;
            await DeactivatePushAsync(t.Id, ct);
        }

        var desks = await _db.DeskStates.ToListAsync(ct);
        foreach (var d in desks)
        {
            d.CurrentNumber = 0;
            d.UpdatedAt = now;
        }

        state.ActiveDayKey = today;
        state.QueueSequence = 0;
        state.LastCalledNumber = 0;
        state.WaitingCount = 0;
        state.UpdatedAt = now;

        _db.QueueLogs.Add(new QueueLog
        {
            Action = "DAILY_RESET",
            Details = $"إعادة تعيين لليوم {today}",
            Timestamp = now
        });

        await _db.SaveChangesAsync(ct);
    }

    private async Task<SystemState> GetSystemStateAsync(CancellationToken ct)
    {
        var state = await _db.SystemStates.FirstOrDefaultAsync(ct);
        if (state != null)
            return state;

        state = new SystemState
        {
            Id = 1,
            ActiveDayKey = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            IsOpen = true
        };
        _db.SystemStates.Add(state);
        await _db.SaveChangesAsync(ct);
        return state;
    }
}

public class QueueActionResult
{
    public bool Success { get; set; }
    public bool StateChanged { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? ClientNumber { get; set; }
    public int? Desk { get; set; }
    public int? TicketId { get; set; }
    public long LiveEventSeq { get; set; }
    public string? Status { get; set; }
    public int MaxDeskCurrent { get; set; }
    public string? TransferMessage { get; set; }
    public string? CompanyName { get; set; }
    public string? RegNumber { get; set; }
    public int? RegistrationId { get; set; }
    public int DuplicateCount { get; set; } = 1;
    public List<DuplicateMatchInfo> Matches { get; set; } = new();

    /// <summary>
    /// معلومات داخلية فقط لتوجيه الأحداث للعملاء المعنيين. لا تُرسل إلى المتصفح الموظف.
    /// </summary>
    [JsonIgnore]
    public List<TicketNotificationInfo> Notifications { get; set; } = new();
}

public class DuplicateMatchInfo
{
    public int TicketId { get; set; }
    public int? RegistrationId { get; set; }
    [JsonIgnore]
    public string TokenHash { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string RegNumber { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class TicketNotificationInfo
{
    public int TicketId { get; set; }
    [JsonIgnore]
    public string TokenHash { get; set; } = "";
    public int ClientNumber { get; set; }
    public string Status { get; set; } = "";
    public int? Desk { get; set; }
    public long LiveEventSeq { get; set; }
    public bool WasServed { get; set; }
    public int? NearRemaining { get; set; }
    public int MaxDeskCurrent { get; set; }
}
