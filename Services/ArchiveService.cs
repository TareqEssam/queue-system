using Microsoft.EntityFrameworkCore;
using QueueSystem.Data;
using QueueSystem.Data.Entities;

namespace QueueSystem.Services;

public class ArchiveService
{
    private readonly AppDbContext _db;

    public ArchiveService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// بحث في الأرشيف باسم الشركة أو رقم السجل التجاري أو رقم العميل.
    /// </summary>
    public async Task<List<ArchiveRecord>> SearchAsync(
        string? companyName = null,
        string? commercialRegister = null,
        int? clientNumber = null,
        DateTime? from = null,
        DateTime? to = null,
        int take = 100,
        CancellationToken ct = default)
    {
        var query = _db.ArchiveRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(companyName))
        {
            var term = companyName.Trim();
            query = query.Where(a => a.CompanyName.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(commercialRegister))
        {
            var term = commercialRegister.Trim();
            query = query.Where(a => a.CommercialRegister.Contains(term));
        }

        if (clientNumber.HasValue)
            query = query.Where(a => a.ClientNumber == clientNumber.Value);

        if (from.HasValue)
            query = query.Where(a => a.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(a => a.Timestamp <= to.Value);

        return await query
            .OrderByDescending(a => a.Timestamp)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);
    }
}
