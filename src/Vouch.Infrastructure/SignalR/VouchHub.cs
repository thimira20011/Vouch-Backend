using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.SignalR;

[Authorize]
public class VouchHub(IApplicationDbContext db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!Guid.TryParse(userId, out var id)) throw new HubException("Authentication is required.");
        await RequireEligibleAsync(id);
        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }
        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(string conversationId)
    {
        if (!Guid.TryParse(Context.UserIdentifier, out var id)) throw new HubException("Authentication is required.");
        await RequireEligibleAsync(id);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"conv_{conversationId}");
    }

    private async Task RequireEligibleAsync(Guid id)
    {
        try { await MemberEligibility.RequireActiveAsync(db, id, Context.ConnectionAborted); }
        catch (Vouch.Application.Common.EligibilityException) { throw new HubException("Verified onboarding and an active account are required."); }
    }

    public async Task LeaveConversation(string conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"conv_{conversationId}");
    }
}
