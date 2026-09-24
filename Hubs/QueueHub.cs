using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace QueueSystem.Hubs;

/// <summary>Hub للموظفين — يتطلب JWT.</summary>
[Authorize]
public class QueueHub : Hub
{
    public async Task JoinDesk(int desk)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"desk-{desk}");
        await Groups.AddToGroupAsync(Context.ConnectionId, "staff");
    }

    public async Task JoinManager()
    {
        var role = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        if (!string.Equals(role, "Manager", StringComparison.OrdinalIgnoreCase))
            throw new HubException("غير مصرح.");
        await Groups.AddToGroupAsync(Context.ConnectionId, "managers");
        await Groups.AddToGroupAsync(Context.ConnectionId, "staff");
    }
}

/// <summary>Hub عام للعملاء — أحداث التذكرة فقط عبر مجموعة token hash.</summary>
public class PublicQueueHub : Hub
{
    public Task JoinSystem() => Groups.AddToGroupAsync(Context.ConnectionId, "public-system");

    public Task JoinTicket(string tokenHash)
    {
        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length < 16 || tokenHash.Length > 128)
            throw new HubException("رمز غير صالح.");
        return Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{tokenHash.Trim()}");
    }
}
