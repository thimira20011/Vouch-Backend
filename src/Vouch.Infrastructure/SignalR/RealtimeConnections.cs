using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Vouch.Application.Common.Interfaces;

namespace Vouch.Infrastructure.SignalR;

public sealed class RealtimeConnections : IRealtimeConnections
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    public sealed record Connection(HubCallerContext Context, Guid UserId, Guid SessionId, DateTimeOffset ExpiresAt)
    {
        public ConcurrentDictionary<Guid, byte> Conversations { get; } = new();
    }
    public void Add(HubCallerContext context)
    {
        var principal = context.User!;
        _connections[context.ConnectionId] = new(context, Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Guid.Parse(principal.FindFirstValue("sid")!), DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirstValue("exp")!)));
    }
    public Connection[] Snapshot() => _connections.Values.ToArray();
    public void Remove(string id) => _connections.TryRemove(id, out _);
    public void Join(string id, Guid conversation) { if (_connections.TryGetValue(id, out var c)) c.Conversations[conversation] = 0; }
    public void Leave(string id, Guid conversation) { if (_connections.TryGetValue(id, out var c)) c.Conversations.TryRemove(conversation, out _); }
    public void RevokeUser(Guid userId) { foreach (var c in Snapshot().Where(c => c.UserId == userId)) Abort(c); }
    public void RevokeSession(Guid sessionId) { foreach (var c in Snapshot().Where(c => c.SessionId == sessionId)) Abort(c); }
    public void RevokeConversation(Guid conversationId)
    {
        foreach (var c in Snapshot()) c.Conversations.TryRemove(conversationId, out _);
    }
    public void Abort(Connection connection) { Remove(connection.Context.ConnectionId); connection.Context.Abort(); }
}
