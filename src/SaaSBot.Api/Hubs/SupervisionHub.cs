using Microsoft.AspNetCore.SignalR;

namespace SaaSBot.Api.Hubs;

/// <summary>
/// SupervisionHub — real-time channel between the backend and the merchant dashboard.
/// When a conversation needs human intervention, the backend pushes an alert here.
/// The dashboard connects with the TenantId as a group identifier so each merchant
/// only sees their own escalations.
/// </summary>
public sealed class SupervisionHub : Hub
{
    /// <summary>Dashboard client calls this to join their tenant's supervision group.</summary>
    public async Task JoinTenantGroup(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out _))
        {
            await Clients.Caller.SendAsync("Error", "Invalid tenant ID.");
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
        await Clients.Caller.SendAsync("Joined", tenantId);
    }

    public async Task LeaveTenantGroup(string tenantId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"tenant:{tenantId}");
    }
}
