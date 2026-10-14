using System.Text.Json;
using Vouch.Domain.Entities;

namespace Vouch.Application.Features.Notifications;

public record NotificationDto(Guid Id, NotificationKind Kind, JsonElement Payload, DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt, DateTimeOffset? DeadlineAt);
public record NotificationPage(IReadOnlyList<NotificationDto> Items, int TotalCount, int UnreadCount, int Page, int PageSize);
public record NotificationDeliveryStatus(int PendingEmails, int OverdueEmails, int RecoveredLate, int PendingWithoutArchitect,
    DateTimeOffset? OldestPendingDeadline);
public interface INotificationService
{
    Task<NotificationPage> GetAsync(Guid userId, int page = 1, int pageSize = 20, bool unreadOnly = false, CancellationToken ct = default);
    Task ReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default);
    Task<NotificationDeliveryStatus> GetDeliveryStatusAsync(Guid architectId, CancellationToken ct = default);
}
