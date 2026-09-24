using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Notifications;

namespace RequestPortal.Application.Notifications;

public sealed record NotificationPreferenceView(
    NotificationChannel Channel,
    string Destination,
    bool Enabled);

public sealed record OrganizationNotificationSubscriptionView(
    Guid MembershipId,
    Guid OrganizationId,
    string OrganizationName,
    string Role,
    bool ReceiveOrganizationNotifications,
    bool ReceiveRequestNotifications,
    bool ReceiveMembershipNotifications);

public sealed record NotificationHistoryView(
    Guid Id,
    NotificationChannel Channel,
    string Destination,
    string Subject,
    NotificationDeliveryStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SentUtc,
    string? LastError);

public interface INotificationPreferenceService
{
    Task EnsureDefaultEmailPreferenceAsync(
        Guid userId,
        string? registeredEmail,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationPreferenceView>> GetPreferencesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task SavePreferenceAsync(
        Guid userId,
        NotificationChannel channel,
        string destination,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationNotificationSubscriptionView>> GetOrganizationSubscriptionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task SaveOrganizationSubscriptionAsync(
        Guid userId,
        Guid membershipId,
        bool receiveOrganizationNotifications,
        bool receiveRequestNotifications,
        bool receiveMembershipNotifications,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationHistoryView>> GetRecentHistoryAsync(
        Guid userId,
        int take = 25,
        CancellationToken cancellationToken = default);
}
