using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common.Interfaces;
using Vouch.Infrastructure.Security;
using Vouch.Application.Common;
using Vouch.Domain.Entities;
using Vouch.Domain.Enums;

namespace Vouch.Infrastructure.SignalR;

[Authorize]
public class VouchHub(IApplicationDbContext db, RealtimeConnections connections, TimeProvider clock, CampusBoundary boundary) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await RequireCurrentAsync();
        connections.Add(Context);
        await base.OnConnectedAsync();
    }

    public async Task JoinConversation(string conversationId)
    {
        var user = await RequireCurrentAsync();
        if (!Guid.TryParse(conversationId, out var id)) throw new HubException("Conversation is unavailable.");
        try { await ResourceAccess.RequireConversationAsync(db, user.Id, id, Context.ConnectionAborted); }
        catch (EligibilityException) { throw new HubException("Conversation is unavailable."); }
        connections.Join(Context.ConnectionId, id);
    }

    private async Task<User> RequireCurrentAsync()
    {
        try
        {
            var user = await SessionAccess.RequireAsync(db, Context.User!, clock.GetUtcNow(), Context.ConnectionAborted);
            await boundary.RequireAsync(db, user.CampusId, Context.ConnectionAborted);
            if (user.Role != UserRole.Architect) MemberEligibility.RequireActive(user);
            return user;
        }
        catch (Exception ex) when (ex is EligibilityException or UnauthorizedAccessException)
        {
            Context.Abort();
            throw new HubException("Session or account is unavailable.");
        }
    }

    public async Task LeaveConversation(string conversationId)
    {
        await RequireCurrentAsync();
        if (!Guid.TryParse(conversationId, out var id)) throw new HubException("Conversation is unavailable.");
        connections.Leave(Context.ConnectionId, id);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
