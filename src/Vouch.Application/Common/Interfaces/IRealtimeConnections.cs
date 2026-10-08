namespace Vouch.Application.Common.Interfaces;

public interface IRealtimeConnections
{
    void RevokeUser(Guid userId);
    void RevokeSession(Guid sessionId);
    void RevokeConversation(Guid conversationId);
}
