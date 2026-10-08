using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common;
using Vouch.Application.Common.Interfaces;
using Vouch.Domain.Enums;
using Vouch.Infrastructure.Security;

namespace Vouch.Infrastructure.SignalR;

public sealed class RealtimeDelivery(IApplicationDbContext db, RealtimeConnections connections, IHubContext<VouchHub> hub, TimeProvider clock, CampusBoundary boundary)
{
    public async Task SendAsync(string method, object payload, Guid? userId = null, Guid? conversationId = null,
        Guid? architectCampusId = null, bool subscriptionRequired = false, CancellationToken ct = default)
    {
        // Group membership and a cached authenticated principal are not access decisions.
        foreach (var connection in connections.Snapshot())
        {
            if (userId is not null && connection.UserId != userId ||
                subscriptionRequired && conversationId is not null && !connection.Conversations.ContainsKey(conversationId.Value)) continue;
            try
            {
                if (connection.ExpiresAt <= clock.GetUtcNow()) throw new UnauthorizedAccessException();
                var user = await SessionAccess.RequireAsync(db, connection.Context.User!, clock.GetUtcNow(), ct);
                await boundary.RequireAsync(db, user.CampusId, ct);
                if (architectCampusId is not null)
                {
                    if (user.Role != UserRole.Architect || user.Status != AccountStatus.Active || user.CampusId != architectCampusId) continue;
                }
                else
                {
                    MemberEligibility.RequireActive(user);
                    if (conversationId is not null) await ResourceAccess.RequireConversationAsync(db, user.Id, conversationId.Value, ct);
                }
                await hub.Clients.Client(connection.Context.ConnectionId).SendCoreAsync(method, [payload], ct);
            }
            catch (UnauthorizedAccessException) { connections.Abort(connection); }
            catch (EligibilityException)
            {
                if (conversationId is not null) connections.Leave(connection.Context.ConnectionId, conversationId.Value);
            }
        }
    }
}
