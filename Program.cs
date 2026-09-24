using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using QueueSystem.Core;
using QueueSystem.Data;
using QueueSystem.Data.Entities;
using QueueSystem.Hubs;
using QueueSystem.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=queue.db"));

// ── Services ──────────────────────────────────────────────
builder.Services.AddSingleton<QueueLock>();
builder.Services.AddScoped<QueueEngine>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<ArchiveService>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddScoped<TicketQueryService>();
builder.Services.AddSingleton<FcmService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FcmService>());
builder.Services.AddHostedService<BackupService>();
builder.Services.AddHttpClient("fcm");
builder.Services.AddHostedService<TunnelService>(); // Cloudflare Tunnel (Outbound فقط)

// ── SignalR (تحديث لحظي) ──────────────────────────────────
builder.Services.AddSignalR();

// ── JWT Authentication ────────────────────────────────────
// ── JWT: رفض الأسرار المعروفة + توليد تلقائي عند أول تشغيل ──
static bool IsInsecureJwtSecret(string? s)
{
    if (string.IsNullOrWhiteSpace(s) || s.Length < 32) return true;
    var v = s.Trim();
    if (v.StartsWith("CHANGE_THIS", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Contains("replace-me", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Contains("example", StringComparison.OrdinalIgnoreCase)) return true;
    if (v.Contains("your-secret", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

var jwtSecret = builder.Configuration["Security:JwtSecret"]?.Trim();
var contentRoot = builder.Environment.ContentRootPath;
var secretsPath = Path.Combine(contentRoot, "data", "jwt.secret");

if (IsInsecureJwtSecret(jwtSecret))
{
    Directory.CreateDirectory(Path.Combine(contentRoot, "data"));
    if (File.Exists(secretsPath))
    {
        jwtSecret = File.ReadAllText(secretsPath).Trim();
    }
    if (IsInsecureJwtSecret(jwtSecret))
    {
        jwtSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        File.WriteAllText(secretsPath, jwtSecret);
        Console.WriteLine("=================================================");
        Console.WriteLine(" تم توليد JwtSecret عشوائي وحفظه في data/jwt.secret");
        Console.WriteLine(" لا تشارك هذا الملف. انسخه مع النسخ الاحتياطي.");
        Console.WriteLine("=================================================");
    }
}

// ضمان أن AuthService والـ middleware يستخدمان نفس السر
builder.Configuration["Security:JwtSecret"] = jwtSecret;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        // لدعم SignalR مع JWT
        opt.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ── Rate limiting على المسارات العامة ──
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("publicWrite", lim =>
    {
        lim.Window = TimeSpan.FromMinutes(1);
        lim.PermitLimit = 30;
        lim.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("auth", lim =>
    {
        lim.Window = TimeSpan.FromMinutes(1);
        lim.PermitLimit = 20;
        lim.QueueLimit = 0;
    });
    // سياسة عامة حسب المسار كشبكة أمان
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/public", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3600,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        }
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 15,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                });
        }
        return RateLimitPartition.GetNoLimiter("open");
    });
});

// ── CORS (للتطوير والوصول من الهواتف) ─────────────────────
var tunnelEnabled = builder.Configuration.GetValue("Tunnel:Enabled", false);
var corsOrigins = (builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                   ?? Array.Empty<string>())
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Select(o => o.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (tunnelEnabled && (
        corsOrigins.Length == 0 ||
        corsOrigins.Any(o => o.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
                             o.Contains("example.com", StringComparison.OrdinalIgnoreCase))))
{
    throw new InvalidOperationException(
        "النفق مفعّل: عيّن Cors:AllowedOrigins بنطاقك الحقيقي فقط (لا تترك REPLACE أو example.com).\n" +
        "مثال: \"AllowedOrigins\": [ \"https://queue.your-domain.com\" ]");
}

builder.Services.AddCors(opt =>
{
    opt.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        if (corsOrigins.Length == 0)
        {
            // تطوير محلي فقط (بدون نفق): localhost
            policy.SetIsOriginAllowed(origin =>
            {
                if (string.IsNullOrEmpty(origin)) return true;
                try
                {
                    var u = new Uri(origin);
                    return u.Host is "localhost" or "127.0.0.1" or "::1";
                }
                catch { return false; }
            });
        }
        else
        {
            policy.WithOrigins(corsOrigins);
        }
    });
});

static void EnsureSqliteColumn(System.Data.Common.DbConnection connection, string table, string column, string definition)
{
    using var check = connection.CreateCommand();
    check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
    var exists = Convert.ToInt32(check.ExecuteScalar()) > 0;
    if (exists) return;

    using var alter = connection.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
    alter.ExecuteNonQuery();
}

var app = builder.Build();

// ── تهيئة قاعدة البيانات عند التشغيل ──────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // ترقية قاعدة البيانات الحالية دون حذف queue.db أو فقد أي بيانات.
    // الإصدار الأول لم يكن يحتوي على Desk داخل Employees؛ نضيف العمود فقط إذا كان مفقودًا.
    try
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name='Desk';";
        var hasDeskColumn = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        if (!hasDeskColumn)
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Employees ADD COLUMN Desk INTEGER NULL;";
            alter.ExecuteNonQuery();
        }
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_Employees_Desk ON Employees(Desk);";
        index.ExecuteNonQuery();

        EnsureSqliteColumn(connection, "Tickets", "LastNearRemainingNotified", "INTEGER NULL");
        EnsureSqliteColumn(connection, "SystemStates", "LastClosedAt", "TEXT NULL");
        EnsureSqliteColumn(connection, "SystemStates", "LastClosedBy", "TEXT NULL");
        EnsureSqliteColumn(connection, "SystemStates", "LastOpenedAt", "TEXT NULL");
        EnsureSqliteColumn(connection, "SystemStates", "LastOpenedBy", "TEXT NULL");
        EnsureSqliteColumn(connection, "PushRegistrations", "Language", "TEXT NOT NULL DEFAULT 'ar'");
        EnsureSqliteColumn(connection, "PushRegistrations", "PushType", "TEXT NOT NULL DEFAULT 'fcm'");
        EnsureSqliteColumn(connection, "PushRegistrations", "Endpoint", "TEXT NULL");
        EnsureSqliteColumn(connection, "PushRegistrations", "P256dh", "TEXT NULL");
        EnsureSqliteColumn(connection, "PushRegistrations", "Auth", "TEXT NULL");

        using var ticketIndex = connection.CreateCommand();
        ticketIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Tickets_ClientNumber_Status ON Tickets(ClientNumber, Status);";
        ticketIndex.ExecuteNonQuery();
        using var regIndex = connection.CreateCommand();
        regIndex.CommandText = "CREATE INDEX IF NOT EXISTS IX_Tickets_RegistrationId ON Tickets(RegistrationId);";
        regIndex.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException("تعذر ترقية قاعدة البيانات لإضافة تخصيص الشباك للباحثين. لم يتم حذف أي بيانات.", ex);
    }

    try
    {
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
        db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
    }
    catch { /* SQLite hardening best-effort */ }

    // إنشاء مدير افتراضي — كلمة مرور إلزامية قوية (لا افتراضي معروف)
    if (!db.Employees.Any())
    {
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var initialPassword = Environment.GetEnvironmentVariable("QUEUE_ADMIN_PASSWORD")
            ?? builder.Configuration["Security:InitialAdminPassword"];

        if (string.IsNullOrWhiteSpace(initialPassword)
            || initialPassword == "Admin@12345"
            || initialPassword.Length < 10)
        {
            throw new InvalidOperationException(
                "يجب تعيين كلمة مرور مدير قوية قبل التشغيل:\n" +
                "  متغير البيئة QUEUE_ADMIN_PASSWORD أو Security:InitialAdminPassword في appsettings\n" +
                "  (لا يُقبل Admin@12345 ولا أي كلمة أقصر من 10 أحرف)");
        }

        await auth.CreateEmployeeAsync(
            username: "admin",
            password: initialPassword,
            displayName: "المدير العام",
            role: "Manager",
            desk: null);

        Console.WriteLine("=================================================");
        Console.WriteLine(" تم إنشاء حساب المدير: admin");
        Console.WriteLine(" احفظ كلمة المرور في مكان آمن — لن تُعرض هنا.");
        Console.WriteLine("=================================================");
    }
    else if (tunnelEnabled)
    {
        // منع تشغيل النفق إن كان هناك حساب بكلمة المرور الافتراضية التاريخية
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var adminUser = await db.Employees.FirstOrDefaultAsync(e => e.Username == "admin");
        if (adminUser != null && auth.VerifyPassword("Admin@12345", adminUser.PasswordHash))
        {
            throw new InvalidOperationException(
                "النفق مفعّل وحساب admin ما زال بكلمة المرور الافتراضية Admin@12345.\n" +
                "غيّر كلمة المرور محلياً أولاً (بدون نفق أو من الجهاز) قبل تعريض النظام للعامة.");
        }
    }

    // تهيئة الشباكين إن لم يكونا موجودين
    var desks = builder.Configuration.GetSection("Queue:Desks").Get<int[]>() ?? new[] { 11, 12 };
    foreach (var d in desks)
    {
        if (!db.DeskStates.Any(x => x.Desk == d))
        {
            db.DeskStates.Add(new DeskState { Desk = d, CurrentNumber = 0, UpdatedAt = DateTime.UtcNow });
        }
    }
    await db.SaveChangesAsync();
}

app.UseCors();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// ── SignalR Hub ───────────────────────────────────────────
app.MapHub<QueueHub>("/hubs/queue");
app.MapHub<PublicQueueHub>("/hubs/public");

// ── Minimal APIs (أساسية للبداية) ─────────────────────────

// صحة النظام
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    time = DateTime.UtcNow,
    version = "1.0.0"
}));

// تسجيل دخول
app.MapPost("/api/auth/login", async (LoginRequest req, AuthService auth) =>
{
    var (success, message, token, employee) = await auth.LoginAsync(req.Username, req.Password);
    if (!success)
        return Results.Json(new { success = false, message }, statusCode: 401);

    return Results.Ok(new
    {
        success = true,
        message,
        token,
        employee = new
        {
            employee!.Id,
            employee.Username,
            employee.DisplayName,
            employee.Role,
            desk = employee.Desk
        }
    });
});

// إنشاء موظف (مدير فقط)
app.MapPost("/api/auth/create-employee", async (CreateEmployeeRequest req, AuthService auth, HttpContext ctx) =>
{
    var current = await GetCurrentEmployeeAsync(ctx, auth);
    if (current == null || current.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var (success, message) = await auth.CreateEmployeeAsync(req.Username, req.Password, req.DisplayName, req.Role, req.Desk);
    return success
        ? Results.Ok(new { success, message })
        : Results.BadRequest(new { success, message });
});

// ── واجهة عامة (تسجيل العميل) ─────────────────────────────

// رمز حماية الفورم (ضد الإرسال الآلي)
app.MapGet("/api/public/form-guard", (RegistrationService reg) =>
{
    var token = reg.IssueGuardToken();
    return Results.Ok(new { token });
});

// تسجيل عميل جديد (نفس منطق المشروع الأصلي)
app.MapPost("/api/public/register", async (RegistrationRequest req, RegistrationService reg, HttpContext ctx, IHubContext<QueueHub> hub) =>
{
    var result = await reg.SubmitAsync(req);
    if (!result.Success)
        return Results.BadRequest(new { success = false, message = result.Message });

    var origin = BuildPublicBaseUrl(ctx, builder.Configuration);
    result.TrackUrl = $"{origin}/track/?ticket={result.Ticket}&token={Uri.EscapeDataString(result.TrackToken)}";

    await hub.Clients.Group("staff").SendAsync("QueueUpdated", new
    {
        eventType = "REGISTER",
        clientNumber = result.Ticket,
        registrationId = result.RegistrationId,
        companyName = result.CompanyName,
        regNumber = result.RegNumber,
        status = TicketStatuses.Waiting,
        waitingCount = result.WaitingCount,
        liveEventSeq = result.LiveEventSeq
    });

    return Results.Ok(new
    {
        success = true,
        message = result.Message,
        ticket = result.Ticket,
        trackToken = result.TrackToken,
        trackUrl = result.TrackUrl
    });
}).RequireRateLimiting("publicWrite");

// حالة استقبال العملاء العامة — تستخدمها شاشة التسجيل لتحديث زر الإرسال فورياً.
app.MapGet("/api/public/system-status", async (AppDbContext db) =>
{
    var state = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync();
    return Results.Ok(new
    {
        success = true,
        isOpen = state?.IsOpen ?? true,
        lastClosedAt = state?.LastClosedAt,
        lastClosedBy = state?.LastClosedBy,
        lastOpenedAt = state?.LastOpenedAt,
        lastOpenedBy = state?.LastOpenedBy,
        updatedAt = state?.UpdatedAt
    });
});

// حالة تذكرة عامة (للمتابعة)
app.MapGet("/api/public/ticket-status", async (int ticket, string token, string? lang, TicketQueryService query) =>
{
    var data = await query.GetPublicStatusAsync(ticket, token, lang);
    if (data == null)
        return Results.Json(new { success = false, message = "التذكرة غير موجودة أو الرمز غير صحيح." }, statusCode: 404);
    return Results.Ok(data);
});

// تسجيل جهاز للإشعارات
app.MapPost("/api/public/push-register", async (PushRegisterRequest req, TicketQueryService query) =>
{
    if (!int.TryParse(req.Ticket, out var num))
        return Results.BadRequest(new { success = false, message = "رقم غير صالح." });

    var (ok, msg) = await query.RegisterPushAsync(
        num, req.Token ?? "", req.FcmToken ?? "", req.Lang, req.PushType, req.Endpoint, req.P256dh, req.Auth);
    return ok
        ? Results.Ok(new { success = true, message = msg })
        : Results.BadRequest(new { success = false, message = msg });
}).RequireRateLimiting("publicWrite");

app.MapPost("/api/public/push-unregister", async (PushRegisterRequest req, TicketQueryService query) =>
{
    if (!int.TryParse(req.Ticket, out var num))
        return Results.BadRequest(new { success = false, message = "رقم غير صالح." });

    var ok = await query.UnregisterPushAsync(
        num, req.Token ?? "", req.FcmToken ?? "", req.PushType, req.Endpoint);
    return Results.Ok(new { success = true, removed = ok });
}).RequireRateLimiting("publicWrite");

// استدعاء التالي
app.MapPost("/api/queue/next", async (DeskRequest req, QueueEngine engine, AuthService auth, HttpContext ctx, IHubContext<QueueHub> hub, IHubContext<PublicQueueHub> publicHub, FcmService fcm, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null) return Results.Unauthorized();
    if (employee.Role != "Employee" || !employee.Desk.HasValue)
        return Results.Json(new { success = false, message = "هذا الحساب غير مخصص لشباك باحث." }, statusCode: 403);

    var result = await engine.CallNextAsync(employee, employee.Desk.Value);
    await PublishQueueResultAsync(result, employee.Desk.Value, hub, publicHub, fcm, db);
    return Results.Ok(result);
});

// استدعاء رقم محدد
app.MapPost("/api/queue/call-specific", async (CallSpecificRequest req, QueueEngine engine, AuthService auth, HttpContext ctx, IHubContext<QueueHub> hub, IHubContext<PublicQueueHub> publicHub, FcmService fcm, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null) return Results.Unauthorized();
    if (employee.Role != "Employee" || !employee.Desk.HasValue)
        return Results.Json(new { success = false, message = "هذا الحساب غير مخصص لشباك باحث." }, statusCode: 403);

    var result = await engine.CallSpecificAsync(employee, employee.Desk.Value, req.ClientNumber);
    await PublishQueueResultAsync(result, employee.Desk.Value, hub, publicHub, fcm, db);
    return Results.Ok(result);
});

// تخطي
app.MapPost("/api/queue/skip", async (DeskRequest req, QueueEngine engine, AuthService auth, HttpContext ctx, IHubContext<QueueHub> hub, IHubContext<PublicQueueHub> publicHub, FcmService fcm, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null) return Results.Unauthorized();
    if (employee.Role != "Employee" || !employee.Desk.HasValue)
        return Results.Json(new { success = false, message = "هذا الحساب غير مخصص لشباك باحث." }, statusCode: 403);

    var result = await engine.SkipCurrentAsync(employee, employee.Desk.Value);
    await PublishQueueResultAsync(result, employee.Desk.Value, hub, publicHub, fcm, db);
    return Results.Ok(result);
});

// === توافق واجهات الأصل (Employee / Manager) ===
app.MapGet("/api/compat/employee-bootstrap", async (AuthService auth, HttpContext ctx, AppDbContext db, IConfiguration config) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null) return Results.Unauthorized();
    if (employee.Role != "Employee" || !employee.Desk.HasValue)
        return Results.Json(new { success = false, message = "الحساب غير مرتبط بشباك." }, statusCode: 403);

    var desks = await db.DeskStates.AsNoTracking().OrderBy(d => d.Desk).ToListAsync();
    var waiting = await db.Tickets.AsNoTracking().CountAsync(x => x.Status == TicketStatuses.Waiting || x.Status == TicketStatuses.Skipped);
    var state = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync();
    var today = DateTime.UtcNow.Date;
    var callCounts = await db.QueueLogs.AsNoTracking()
        .Where(x => x.Timestamp >= today && (x.Action == "NEXT" || x.Action == "MANUAL") && x.Desk != null)
        .GroupBy(x => x.Desk!.Value)
        .Select(g => new { desk = g.Key, calls = g.Count() })
        .ToDictionaryAsync(x => x.desk, x => x.calls);

    var metricsDesks = desks.ToDictionary(
        d => d.Desk.ToString(),
        d => new { calls = callCounts.TryGetValue(d.Desk, out var calls) ? calls : 0 });

    var recent = await db.Registrations.AsNoTracking().Where(r => r.CreatedAt >= today).OrderByDescending(r => r.Id).Take(60)
        .Select(r => new { row = r.Id, time = r.CreatedAt, regNumber = r.CommercialRegister, companyName = r.CompanyName, clientNumber = r.ClientNumber.ToString(), source = r.Source })
        .ToListAsync();

    var myDesk = desks.FirstOrDefault(d => d.Desk == employee.Desk.Value);
    var allowedDesks = config.GetSection("Queue:Desks").Get<int[]>() ?? new[] { 11, 12 };
    var queue = desks.Select(d => new { desk = d.Desk.ToString(), current = d.CurrentNumber, updatedAt = d.UpdatedAt, updatedBy = d.UpdatedBy }).ToList();

    return Results.Ok(new
    {
        employee = new { username = employee.Username, desk = employee.Desk.Value.ToString(), role = employee.Role.ToLowerInvariant(), displayName = employee.DisplayName },
        systemOpen = state?.IsOpen ?? true,
        system = new { isOpen = state?.IsOpen ?? true, lastClosedAt = state?.LastClosedAt, lastClosedBy = state?.LastClosedBy, lastOpenedAt = state?.LastOpenedAt, lastOpenedBy = state?.LastOpenedBy, updatedAt = state?.UpdatedAt },
        queue = new { desks = queue },
        metrics = new { waitingCount = waiting, desks = metricsDesks },
        recent,
        allowedDesks,
        defaultSearchNumber = myDesk != null && myDesk.CurrentNumber > 0 ? myDesk.CurrentNumber.ToString() : null
    });
});

app.MapGet("/api/compat/manager-bootstrap", async (AuthService auth, HttpContext ctx, AppDbContext db, IConfiguration config) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null || employee.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var desks = await db.DeskStates.AsNoTracking().OrderBy(d => d.Desk).ToListAsync();
    var waiting = await db.Tickets.AsNoTracking().CountAsync(x => x.Status == TicketStatuses.Waiting || x.Status == TicketStatuses.Skipped);
    var state = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync();
    var today = DateTime.UtcNow.Date;

    var callCounts = await db.QueueLogs.AsNoTracking()
        .Where(x => x.Timestamp >= today && (x.Action == "NEXT" || x.Action == "MANUAL") && x.Desk != null)
        .GroupBy(x => x.Desk!.Value)
        .Select(g => new { desk = g.Key, calls = g.Count() })
        .ToDictionaryAsync(x => x.desk, x => x.calls);

    var metricsDesks = desks.ToDictionary(d => d.Desk.ToString(), d => new { calls = callCounts.TryGetValue(d.Desk, out var calls) ? calls : 0 });

    var transferred = await db.Tickets.AsNoTracking()
        .Where(t => t.Status == TicketStatuses.Transferred && t.TransferredAt >= today)
        .OrderByDescending(t => t.TransferredAt)
        .Take(200)
        .ToListAsync();
    var regIds = transferred.Where(t => t.RegistrationId != null).Select(t => t.RegistrationId!.Value).Distinct().ToList();
    var regs = await db.Registrations.AsNoTracking().Where(r => regIds.Contains(r.Id)).ToListAsync();
    var regsById = regs.ToDictionary(r => r.Id);

    var registrationsToday = await db.Registrations.AsNoTracking()
        .Where(r => r.CreatedAt >= today)
        .OrderByDescending(r => r.Id)
        .Take(200)
        .ToListAsync();
    var todayRegIds = registrationsToday.Select(r => r.Id).ToList();
    var todayTickets = await db.Tickets.AsNoTracking().Where(t => todayRegIds.Contains(t.RegistrationId ?? 0)).ToListAsync();
    var ticketByReg = todayTickets.GroupBy(t => t.RegistrationId).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Id).First());

    var queue = desks.Select(d => new { desk = d.Desk.ToString(), current = d.CurrentNumber, updatedAt = d.UpdatedAt, updatedBy = d.UpdatedBy }).ToList();

    return Results.Ok(new
    {
        employee = new { username = employee.Username, role = "manager", displayName = employee.DisplayName },
        manager = new { username = employee.Username, role = "manager", displayName = employee.DisplayName },
        systemOpen = state?.IsOpen ?? true,
        system = new { isOpen = state?.IsOpen ?? true, lastClosedAt = state?.LastClosedAt, lastClosedBy = state?.LastClosedBy, lastOpenedAt = state?.LastOpenedAt, lastOpenedBy = state?.LastOpenedBy, updatedAt = state?.UpdatedAt },
        metrics = new { waitingCount = waiting, desks = metricsDesks },
        queue = new { desks = queue },
        transfers = transferred.Select(t => new
        {
            clientNumber = t.ClientNumber.ToString(),
            companyName = t.RegistrationId.HasValue && regsById.TryGetValue(t.RegistrationId.Value, out var reg) ? reg.CompanyName : "",
            regNumber = t.RegistrationId.HasValue && regsById.TryGetValue(t.RegistrationId.Value, out var reg2) ? reg2.CommercialRegister : "",
            time = t.TransferredAt,
            note = t.TransferNote,
            transferredBy = t.TransferredBy,
            calledDesk = t.Desk
        }),
        transferred = transferred.Select(t => new { clientNumber = t.ClientNumber.ToString(), time = t.TransferredAt, note = t.TransferNote, transferredBy = t.TransferredBy, calledDesk = t.Desk }),
        recent = registrationsToday.Select(r =>
        {
            ticketByReg.TryGetValue(r.Id, out var t);
            return new
            {
                registrationId = r.Id,
                time = r.CreatedAt,
                regNumber = r.CommercialRegister,
                companyName = r.CompanyName,
                clientNumber = r.ClientNumber.ToString(),
                source = r.Source,
                status = t?.Status ?? TicketStatuses.Waiting,
                calledBy = t?.TransferredBy,
                calledDesk = t?.Desk
            };
        }),
        closeAudit = new { lastClosedAt = state?.LastClosedAt, lastClosedBy = state?.LastClosedBy, lastOpenedAt = state?.LastOpenedAt, lastOpenedBy = state?.LastOpenedBy }
    });
});

// بحث تسجيلات برقم العميل (للباحث)
app.MapGet("/api/employee/lookup", async (int clientNumber, AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null)
        return Results.Unauthorized();

    // كل تسجيلات هذا الرقم في يوم العمل الحالي، مع الاحتفاظ بكل التكرارات.
    // لا نخلط تسجيلات الأيام السابقة مع التذكرة الحالية.
    var today = DateTime.UtcNow.Date;
    var regs = await db.Registrations.AsNoTracking()
        .Where(r => r.ClientNumber == clientNumber && r.CreatedAt >= today)
        .OrderByDescending(r => r.Id)
        .ToListAsync();

    var tickets = await db.Tickets.AsNoTracking()
        .Where(t => t.ClientNumber == clientNumber && t.CreatedAt >= today)
        .OrderByDescending(t => t.Id)
        .ToListAsync();

    var rows = new List<object>();

    foreach (var r in regs)
    {
        var ticket = tickets.FirstOrDefault(t => t.RegistrationId == r.Id);
        rows.Add(new
        {
            registrationId = r.Id,
            clientNumber = r.ClientNumber,
            companyName = r.CompanyName,
            regNumber = r.CommercialRegister,
            source = r.Source,
            time = r.CreatedAt,
            status = ticket != null ? ticket.Status : "WAITING",
            desk = ticket != null ? ticket.Desk : null,
            ticketId = ticket != null ? (int?)ticket.Id : null
        });
    }

    // إن وُجدت تذاكر بلا تسجيل مطابق (حالات قديمة) أظهرها أيضاً
    if (rows.Count == 0 && tickets.Count > 0)
    {
        foreach (var ticket in tickets)
        {
            Registration? reg = null;
            if (ticket.RegistrationId.HasValue)
                reg = await db.Registrations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ticket.RegistrationId.Value);

            rows.Add(new
            {
                registrationId = reg?.Id ?? ticket.RegistrationId ?? 0,
                clientNumber = ticket.ClientNumber,
                companyName = reg?.CompanyName ?? "",
                regNumber = reg?.CommercialRegister ?? "",
                source = reg?.Source ?? "",
                time = reg?.CreatedAt ?? ticket.CreatedAt,
                status = ticket.Status,
                desk = ticket.Desk,
                ticketId = (int?)ticket.Id
            });
        }
    }

    return Results.Ok(rows);
});

// إدارة الباحثين — المدير فقط
app.MapGet("/api/manager/employees", async (AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var manager = await GetCurrentEmployeeAsync(ctx, auth);
    if (manager == null || manager.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var employees = await db.Employees.AsNoTracking()
        .OrderBy(e => e.Role).ThenBy(e => e.DisplayName)
        .Select(e => new
        {
            id = e.Id,
            username = e.Username,
            displayName = e.DisplayName,
            role = e.Role,
            desk = e.Desk,
            isActive = e.IsActive,
            createdAt = e.CreatedAt,
            lastLoginAt = e.LastLoginAt,
            lockedUntil = e.LockUntil
        }).ToListAsync();

    return Results.Ok(new { success = true, employees });
});

app.MapPost("/api/manager/employees", async (CreateEmployeeRequest req, AuthService auth, HttpContext ctx) =>
{
    var manager = await GetCurrentEmployeeAsync(ctx, auth);
    if (manager == null || manager.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    if (!string.Equals(req.Role, "Employee", StringComparison.Ordinal))
        return Results.BadRequest(new { success = false, message = "من هذه الشاشة يمكن إنشاء حساب باحث فقط." });

    var (success, message) = await auth.CreateEmployeeAsync(req.Username, req.Password, req.DisplayName, "Employee", req.Desk);
    return success ? Results.Ok(new { success, message }) : Results.BadRequest(new { success, message });
});

app.MapPost("/api/manager/employees/{id:int}/status", async (int id, EmployeeStatusRequest req, AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var manager = await GetCurrentEmployeeAsync(ctx, auth);
    if (manager == null || manager.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
    if (employee == null) return Results.NotFound(new { success = false, message = "الحساب غير موجود." });
    if (employee.Role == "Manager" && employee.Id == manager.Id && !req.IsActive)
        return Results.BadRequest(new { success = false, message = "لا يمكن للمدير تعطيل حسابه الحالي." });

    employee.IsActive = req.IsActive;
    if (!employee.IsActive)
    {
        employee.LockUntil = DateTime.UtcNow.AddYears(10);
    }
    else
    {
        employee.LockUntil = null;
        employee.FailedLoginCount = 0;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { success = true, isActive = employee.IsActive });
});

app.MapPost("/api/manager/employees/{id:int}/desk", async (int id, EmployeeDeskRequest req, AuthService auth, HttpContext ctx, AppDbContext db, IConfiguration config) =>
{
    var manager = await GetCurrentEmployeeAsync(ctx, auth);
    if (manager == null || manager.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var allowed = config.GetSection("Queue:Desks").Get<int[]>() ?? new[] { 11, 12 };
    if (!allowed.Contains(req.Desk))
        return Results.BadRequest(new { success = false, message = $"الشباك غير صالح. المسموح: {string.Join(" أو ", allowed)}." });

    var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id && e.Role == "Employee");
    if (employee == null) return Results.NotFound(new { success = false, message = "حساب الباحث غير موجود." });

    employee.Desk = req.Desk;
    await db.SaveChangesAsync();
    return Results.Ok(new { success = true, desk = employee.Desk });
});

app.MapPost("/api/manager/employees/{id:int}/reset-password", async (int id, ResetEmployeePasswordRequest req, AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var manager = await GetCurrentEmployeeAsync(ctx, auth);
    if (manager == null || manager.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
        return Results.BadRequest(new { success = false, message = "كلمة المرور يجب أن تكون 8 أحرف على الأقل." });

    var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id);
    if (employee == null) return Results.NotFound(new { success = false, message = "الحساب غير موجود." });

    employee.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password, workFactor: 12);
    employee.FailedLoginCount = 0;
    employee.LockUntil = null;
    await db.SaveChangesAsync();
    return Results.Ok(new { success = true, message = "تم تغيير كلمة المرور وإلغاء القفل إن وجد." });
});

// قائمة اليوم للمدير (تسجيلات + محوّلون)
app.MapGet("/api/manager/today", async (AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null || employee.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var today = DateTime.UtcNow.Date;
    var regs = await db.Registrations.AsNoTracking()
        .Where(r => r.CreatedAt >= today)
        .OrderByDescending(r => r.Id)
        .Take(200)
        .ToListAsync();

    var tickets = await db.Tickets.AsNoTracking()
        .Where(t => t.CreatedAt >= today)
        .ToListAsync();

    var list = regs.Select(r =>
    {
        var ticket = tickets.FirstOrDefault(t => t.RegistrationId == r.Id);
        return new
        {
            registrationId = r.Id,
            clientNumber = r.ClientNumber,
            companyName = r.CompanyName,
            regNumber = r.CommercialRegister,
            time = r.CreatedAt,
            status = ticket?.Status ?? "WAITING",
            desk = ticket?.Desk,
            transferNote = ticket?.TransferNote,
            transferredBy = ticket?.TransferredBy
        };
    }).ToList();

    return Results.Ok(list);
});

// تحويل للمدير
app.MapPost("/api/queue/transfer", async (TransferRequest req, QueueEngine engine, AuthService auth, HttpContext ctx, IHubContext<QueueHub> hub, IHubContext<PublicQueueHub> publicHub, FcmService fcm, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null) return Results.Unauthorized();

    var result = await engine.TransferToManagerAsync(employee, req.RegistrationId, req.Note);
    await PublishQueueResultAsync(result, result.Desk ?? employee.Desk ?? 0, hub, publicHub, fcm, db);
    return Results.Ok(result);
});

// بحث في الأرشيف (مدير فقط)
app.MapGet("/api/archive/search", async (
    string? company,
    string? cr,
    int? number,
    DateTime? from,
    DateTime? to,
    ArchiveService archive,
    AuthService auth,
    HttpContext ctx) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null || employee.Role != "Manager")
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var results = await archive.SearchAsync(company, cr, number, from, to);
    return Results.Ok(results);
});

// فتح / إغلاق التسجيل (المدير أو الباحث)
app.MapPost("/api/manager/system-open", async (SystemOpenRequest req, AuthService auth, HttpContext ctx, AppDbContext db, QueueLock queueLock, IHubContext<QueueHub> hub, IHubContext<PublicQueueHub> publicHub) =>
{
    using var _ = await queueLock.AcquireAsync();
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null || (employee.Role != "Manager" && employee.Role != "Employee"))
        return Results.Json(new { success = false, message = "غير مصرح." }, statusCode: 403);

    var state = await db.SystemStates.FirstOrDefaultAsync();
    var now = DateTime.UtcNow;
    if (state == null)
    {
        state = new SystemState { Id = 1, IsOpen = req.IsOpen, ActiveDayKey = now.ToString("yyyy-MM-dd"), UpdatedAt = now };
        db.SystemStates.Add(state);
    }

    state.IsOpen = req.IsOpen;
    state.UpdatedAt = now;
    if (req.IsOpen)
    {
        state.LastOpenedAt = now;
        state.LastOpenedBy = employee.DisplayName;
    }
    else
    {
        state.LastClosedAt = now;
        state.LastClosedBy = employee.DisplayName;
    }
    state.LiveEventSeq++;
    await db.SaveChangesAsync();

    var eventPayload = new
    {
        isOpen = state.IsOpen,
        lastClosedAt = state.LastClosedAt,
        lastClosedBy = state.LastClosedBy,
        lastOpenedAt = state.LastOpenedAt,
        lastOpenedBy = state.LastOpenedBy,
        updatedAt = state.UpdatedAt,
        updatedBy = employee.DisplayName,
        liveEventSeq = state.LiveEventSeq
    };
    await hub.Clients.Group("staff").SendAsync("SystemStateChanged", eventPayload);
    await publicHub.Clients.Group("public-system").SendAsync("SystemStateChanged", eventPayload);

    return Results.Ok(new { success = true, isOpen = state.IsOpen, lastClosedAt = state.LastClosedAt, lastClosedBy = state.LastClosedBy });
});

// إحصائيات سريعة للمدير / الباحث
app.MapGet("/api/manager/stats", async (AuthService auth, HttpContext ctx, AppDbContext db) =>
{
    var employee = await GetCurrentEmployeeAsync(ctx, auth);
    if (employee == null)
        return Results.Unauthorized();

    var waiting = await db.Tickets.CountAsync(t =>
        t.Status == TicketStatuses.Waiting || t.Status == TicketStatuses.Skipped);
    var desks = await db.DeskStates.AsNoTracking().ToListAsync();
    var state = await db.SystemStates.AsNoTracking().FirstOrDefaultAsync();

    return Results.Ok(new
    {
        waiting,
        desks = desks.Select(d => new { d.Desk, d.CurrentNumber, d.UpdatedBy }),
        isOpen = state?.IsOpen ?? true,
        queueSequence = state?.QueueSequence ?? 0
    });
});

// صفحة رئيسية بسيطة
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html lang="ar" dir="rtl">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>نظام الطوابير</title>
  <style>
    body { font-family: system-ui, sans-serif; background:#0f172a; color:#e2e8f0; display:flex; align-items:center; justify-content:center; min-height:100vh; margin:0; }
    .card { background:#1e293b; padding:2rem 2.5rem; border-radius:1rem; text-align:center; box-shadow:0 10px 40px rgba(0,0,0,.4); max-width:420px; }
    h1 { margin:0 0 0.5rem; font-size:1.6rem; }
    p { color:#94a3b8; margin-bottom:1.5rem; }
    a { display:inline-block; margin:0.4rem; padding:0.7rem 1.4rem; background:#3b82f6; color:white; text-decoration:none; border-radius:0.5rem; font-weight:600; }
    a:hover { background:#2563eb; }
    .status { margin-top:1.5rem; font-size:0.85rem; color:#64748b; }
  </style>
</head>
<body>
  <div class="card">
    <h1>نظام الطوابير</h1>
    <p>الإصدار المحلي — Portable</p>
    <a href="/employee/">واجهة الباحث</a>
    <a href="/manager/">واجهة المدير</a>
    <a href="/form/">تسجيل عميل</a>
    <div class="status" id="status">جاري فحص الحالة...</div>
  </div>
  <script>
    try {
      if (localStorage.getItem('cr_track_ticket_v2') && localStorage.getItem('cr_track_token_v2')) {
        location.replace('/track/');
      }
    } catch (_) {}
    fetch('/api/health').then(r => r.json()).then(d => {
      document.getElementById('status').textContent = 'الحالة: ' + d.status + ' | ' + new Date(d.time).toLocaleString('ar');
    }).catch(() => {
      document.getElementById('status').textContent = 'تعذر الاتصال بالخادم';
    });
  </script>
</body>
</html>
""", "text/html; charset=utf-8"));

app.Run();

// ── Helpers ───────────────────────────────────────────────

static string BuildPublicBaseUrl(HttpContext ctx, IConfiguration config)
{
    var configured = config["PublicBaseUrl"]?.Trim().TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(configured))
        return configured;

    // في الاستضافة خلف reverse proxy قد يرى ASP.NET الطلب داخلياً على HTTP
    // رغم أن العميل متصل فعلياً عبر HTTPS. أولوية Origin تمنع إنشاء TrackUrl
    // يبدأ بـ http:// ثم يفشل على الهاتف بسبب Mixed Content / HTTPS policy.
    var origin = ctx.Request.Headers["Origin"].FirstOrDefault()?.Trim().TrimEnd('/');
    if (!string.IsNullOrWhiteSpace(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        && (originUri.Scheme == Uri.UriSchemeHttps || originUri.Scheme == Uri.UriSchemeHttp)
        && !string.IsNullOrWhiteSpace(originUri.Host))
    {
        return origin;
    }

    var forwardedProto = ctx.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
    var forwardedHost = ctx.Request.Headers["X-Forwarded-Host"].FirstOrDefault();
    var scheme = string.IsNullOrWhiteSpace(forwardedProto) ? ctx.Request.Scheme : forwardedProto.Split(',')[0].Trim();
    var host = string.IsNullOrWhiteSpace(forwardedHost) ? ctx.Request.Host.ToString() : forwardedHost.Split(',')[0].Trim();
    return $"{scheme}://{host}".TrimEnd('/');
}

static async Task PublishQueueResultAsync(
    QueueActionResult result,
    int desk,
    IHubContext<QueueHub> hub,
    IHubContext<PublicQueueHub> publicHub,
    FcmService fcm,
    AppDbContext db)
{
    if (!result.StateChanged && !result.Success)
        return;

    await Task.WhenAll(
        hub.Clients.Group($"desk-{desk}").SendAsync("QueueUpdated", result),
        hub.Clients.Group("managers").SendAsync("QueueUpdated", result),
        hub.Clients.Group("staff").SendAsync("QueueUpdated", result));

    var fcmItems = new List<(int TicketId, NotificationCopy Copy, object? Data)>();
    var publicSends = new List<Task>();
    foreach (var evt in result.Notifications)
    {
        if (string.IsNullOrWhiteSpace(evt.TokenHash))
            continue;

        var publicPayload = new
        {
            success = true,
            clientNumber = evt.ClientNumber,
            status = evt.Status,
            desk = evt.Desk,
            maxDeskCurrent = evt.MaxDeskCurrent > 0 ? evt.MaxDeskCurrent : result.MaxDeskCurrent,
            nearRemaining = evt.NearRemaining,
            liveEventSeq = evt.LiveEventSeq
        };
        publicSends.Add(publicHub.Clients.Group($"ticket-{evt.TokenHash}").SendAsync("TicketUpdated", publicPayload));

        fcmItems.Add((evt.TicketId, BuildNotificationCopy(evt, result), new
        {
            status = evt.Status,
            clientNumber = evt.ClientNumber.ToString(),
            desk = evt.Desk?.ToString() ?? "",
            nearRemaining = evt.NearRemaining?.ToString() ?? "",
            liveEventSeq = evt.LiveEventSeq.ToString(),
            ticketId = evt.TicketId.ToString(),
            link = "/track/"
        }));
    }

    if (publicSends.Count > 0)
        await Task.WhenAll(publicSends);
    await fcm.SendToTicketsAsync(fcmItems);

    var terminalIds = result.Notifications
        .Where(n => TicketStatuses.IsTerminal(n.Status))
        .Select(n => n.TicketId)
        .Distinct()
        .ToList();
    if (terminalIds.Count > 0)
    {
        await db.PushRegistrations
            .Where(p => terminalIds.Contains(p.TicketId) && p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
    }
}

static NotificationCopy BuildNotificationCopy(TicketNotificationInfo evt, QueueActionResult result)
{
    return evt.Status switch
    {
        "CALLED" => new NotificationCopy(
            "تم استدعاء رقمك",
            $"توجه الآن إلى الشباك رقم {evt.Desk}.",
            "Your number was called",
            $"Please go to desk {evt.Desk} now."),

        "SKIPPED" => new NotificationCopy(
            "تم تخطي رقمك مؤقتاً",
            "لديك مهلة حتى استدعاء 5 أرقام تالية.",
            "Your number was temporarily skipped",
            "You have a grace period until 5 subsequent numbers are called."),

        "TRANSFERRED" => new NotificationCopy(
            "تم تحويل طلبك إلى الإدارة",
            "يرجى التوجه إلى مكتب الإدارة المحدد من موظف الشباك.",
            "Your request was transferred to administration",
            "Please go to the administration office specified by the desk employee."),

        "SKIPPED_EXPIRED" => new NotificationCopy(
            "انتهت مهلة دورك",
            "يجب سحب رقم جديد من ماكينة الأرقام.",
            "Your grace period has ended",
            "Please take a new ticket from the queue machine."),

        "CLOSED" when evt.WasServed => new NotificationCopy(
            "تم الانتهاء من خدمتك",
            "يمكنك الآن متابعة موقف طلبك من خلال الإدارة.",
            "Your service is complete",
            "You can now follow your request status with the administration."),

        "CLOSED" => new NotificationCopy(
            "انتهى دورك",
            "تم تجاوز رقمك. يجب سحب رقم جديد لإعادة التسجيل.",
            "Your turn has expired",
            "Your number was passed. Please take a new ticket to register again."),

        "NEAR" when evt.NearRemaining == 1 => new NotificationCopy(
            "اقترب دورك",
            "متبقي رقم واحد فقط أمام دورك.",
            "Your turn is near",
            "Only one number remains before your turn."),

        "NEAR" => new NotificationCopy(
            "اقترب دورك",
            "متبقي رقمان فقط أمام دورك.",
            "Your turn is near",
            "Only two numbers remain before your turn."),

        _ => new NotificationCopy("تحديث في الطابور", "تم تحديث حالة طلبك.", "Queue update", "Your request status was updated.")
    };
}

static async Task<Employee?> GetCurrentEmployeeAsync(HttpContext ctx, AuthService auth)
{
    var header = ctx.Request.Headers.Authorization.FirstOrDefault();
    if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return null;

    var token = header["Bearer ".Length..].Trim();
    return await auth.ValidateTokenAsync(token);
}

// ── DTOs ──────────────────────────────────────────────────
record LoginRequest(string Username, string Password);
record CreateEmployeeRequest(string Username, string Password, string DisplayName, string Role, int? Desk);
record EmployeeStatusRequest(bool IsActive);
record EmployeeDeskRequest(int Desk);
record ResetEmployeePasswordRequest(string Password);
record DeskRequest(int Desk);
record CallSpecificRequest(int Desk, int ClientNumber);
record PushRegisterRequest(string? Ticket, string? Token, string? FcmToken, string? Lang, string? PushType, string? Endpoint, string? P256dh, string? Auth);
record SystemOpenRequest(bool IsOpen);
record TransferRequest(int RegistrationId, string? Note);