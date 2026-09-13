using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Notifications;
using RequestPortal.Domain.Notifications;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Notifications;

public sealed class NotificationPreferenceService : INotificationPreferenceService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly NotificationOptions notificationOptions;

    public NotificationPreferenceService(
        RequestPortalDbContext dbContext,
        IOptions<NotificationOptions> notificationOptions)
    {
        this.dbContext = dbContext;
        this.notificationOptions = notificationOptions.Value;
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

    public async Task SavePreferenceAsync(
        Guid userId,
        NotificationChannel channel,
        string destination,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (enabled && !IsCommissioned(channel))
        {
            throw new InvalidOperationException($"{channel} notifications are not commissioned in this Request Portal environment.");
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
            NotificationChannel.Sms => notificationOptions.SmsEnabled,
            NotificationChannel.WhatsApp => notificationOptions.WhatsAppEnabled,
            _ => false
        };
    }
}
