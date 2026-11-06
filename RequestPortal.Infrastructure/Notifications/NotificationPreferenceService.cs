using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Notifications;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Notifications;

public sealed class NotificationPreferenceService : INotificationPreferenceService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly NotificationOptions notificationOptions;
    private readonly IBusinessAuditWriter auditWriter;

    public NotificationPreferenceService(
        RequestPortalDbContext dbContext,
        IOptions<NotificationOptions> notificationOptions,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.notificationOptions = notificationOptions.Value;
        this.auditWriter = auditWriter;
    }

    public async Task EnsureDefaultEmailPreferenceAsync(
        Guid userId,
        string? registeredEmail,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(registeredEmail))
        {
            return;
        }

        bool exists = await dbContext.NotificationPreferences
            .AsNoTracking()
            .AnyAsync(
                item => item.UserId == userId && item.Channel == NotificationChannel.Email,
                cancellationToken);

        if (exists)
        {
            return;
        }

        dbContext.NotificationPreferences.Add(
            new NotificationPreference(
                Guid.NewGuid(),
                userId,
                NotificationChannel.Email,
                registeredEmail.Trim(),
                enabled: true));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationPreferenceView>> GetPreferencesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.NotificationPreferences
            .AsNoTracking()
            .Where(preference => preference.UserId == userId)
            .OrderBy(preference => preference.Channel)
            .Select(preference => new NotificationPreferenceView(
                preference.Channel,
                preference.Destination,
                preference.Enabled))
            .ToListAsync(cancellationToken);
    }

    public static string NormalizeDestination(NotificationChannel channel,string destination) => channel switch
    {
        NotificationChannel.Email => RequestPortal.Common.Contacts.ContactEmail.Normalize(destination)
            ?? throw new ArgumentException("An email address is required."),
        NotificationChannel.WhatsApp => RequestPortal.Common.Contacts.ApplicantTelephone.NormalizeNew(destination)
            ?? throw new ArgumentException("A telephone number is required."),
        _ => destination.Trim()
    };

    public async Task SavePreferenceAsync(
        Guid userId,
        NotificationChannel channel,
        string destination,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        destination=NormalizeDestination(channel,destination);
        if (enabled && !IsCommissioned(channel))
        {
            throw new InvalidOperationException($"{channel} notifications are not commissioned in this Request Portal deployment.");
        }

        NotificationPreference? preference = await dbContext.NotificationPreferences
            .SingleOrDefaultAsync(
                item => item.UserId == userId && item.Channel == channel,
                cancellationToken);

        if (preference is null)
        {
            preference = new NotificationPreference(
                Guid.NewGuid(),
                userId,
                channel,
                destination,
                enabled);

            dbContext.NotificationPreferences.Add(preference);
        }
        else
        {
            preference.Update(destination, enabled);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "NotificationPreferenceChanged",
            "NotificationPreference",
            preference.Id,
            null,
            $"channel={channel}; enabled={enabled}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<OrganizationNotificationSubscriptionView>> GetOrganizationSubscriptionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId &&
                  membership.Status == MembershipStatus.Approved &&
                  membership.Role == OrganizationRole.Administrator
            orderby organization.Name
            select new OrganizationNotificationSubscriptionView(
                membership.Id,
                organization.Id,
                organization.Name,
                membership.Role.ToString(),
                membership.ReceiveOrganizationNotifications,
                membership.ReceiveRequestNotifications,
                membership.ReceiveMembershipNotifications))
            .ToListAsync(cancellationToken);
    }

    public async Task SaveOrganizationSubscriptionAsync(
        Guid userId,
        Guid membershipId,
        bool receiveOrganizationNotifications,
        bool receiveRequestNotifications,
        bool receiveMembershipNotifications,
        CancellationToken cancellationToken = default)
    {
        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(
                item =>
                    item.Id == membershipId &&
                    item.UserId == userId &&
                    item.Status == MembershipStatus.Approved,
                cancellationToken);

        membership.SetNotificationSubscriptions(
            receiveOrganizationNotifications,
            receiveRequestNotifications,
            receiveMembershipNotifications);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "OrganizationNotificationSubscriptionChanged",
            "OrganizationMembership",
            membership.Id,
            membership.OrganizationId,
            $"organization={receiveOrganizationNotifications}; requests={receiveRequestNotifications}; memberships={receiveMembershipNotifications}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationHistoryView>> GetRecentHistoryAsync(
        Guid userId,
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        int boundedTake = Math.Clamp(take, 1, 100);

        return await dbContext.NotificationHistory
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedUtc)
            .Take(boundedTake)
            .Select(item => new NotificationHistoryView(
                item.Id,
                item.Channel,
                item.Destination,
                item.Subject,
                item.Status,
                item.CreatedUtc,
                item.SentUtc,
                item.LastError))
            .ToListAsync(cancellationToken);
    }

    private bool IsCommissioned(NotificationChannel channel)
    {
        return channel switch
        {
            NotificationChannel.Email => notificationOptions.Enabled,
            NotificationChannel.WhatsApp => notificationOptions.WhatsAppEnabled,
            _ => false
        };
    }
}
