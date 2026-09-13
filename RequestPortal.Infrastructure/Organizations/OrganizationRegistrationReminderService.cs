using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Organizations;

public sealed class OrganizationRegistrationReminderService
{
    private readonly RequestPortalDbContext dbContext;

    public OrganizationRegistrationReminderService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<int> QueueDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        DateOnly warningThrough = today.AddDays(60);

        var organizations = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.RegistrationExpiryDate != null &&
                organization.RegistrationExpiryDate <= warningThrough &&
                (organization.Status == OrganizationStatus.Approved || organization.Status == OrganizationStatus.Suspended))
            .Select(organization => new
            {
                organization.Id,
                ExpiryDate = organization.RegistrationExpiryDate!.Value
            })
            .ToListAsync(cancellationToken);

        int queued = 0;
        foreach (var organization in organizations)
        {
            int days = organization.ExpiryDate.DayNumber - today.DayNumber;
            string reminderKind = days <= 0 ? "Expired" : days <= 7 ? "7Days" : days <= 30 ? "30Days" : "60Days";
            Guid messageId = CreateDeterministicGuid(organization.Id, organization.ExpiryDate, reminderKind);

            bool exists = await dbContext.OutboxMessages
                .AsNoTracking()
                .AnyAsync(message => message.Id == messageId, cancellationToken);
            if (exists)
            {
                continue;
            }

            OrganizationRegistrationReminderMessage message = new(
                messageId,
                now,
                organization.Id,
                organization.ExpiryDate,
                reminderKind);

            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                Id = message.MessageId,
                MessageType = typeof(OrganizationRegistrationReminderMessage).FullName
                    ?? nameof(OrganizationRegistrationReminderMessage),
                Payload = JsonSerializer.Serialize(message),
                OccurredUtc = message.OccurredUtc,
                AttemptCount = 0
            });
            queued++;
        }

        if (queued > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return queued;
    }

    private static Guid CreateDeterministicGuid(Guid organizationId, DateOnly expiryDate, string reminderKind)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{organizationId:D}|{expiryDate:yyyy-MM-dd}|{reminderKind}|RegistrationReminder"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
