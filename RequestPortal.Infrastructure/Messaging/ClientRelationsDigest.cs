using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Messaging;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messaging;

public sealed record ClientRelationsDigest(Guid MessageId, DateTimeOffset SlotUtc,
    QuickIntakeClientRelationsEmailRequestedMessage[] Items)
{
    public static DateTimeOffset? SlotFor(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(now, "America/Jamaica");
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || local.Hour < 8 || local.Hour > 16)
            return null;
        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).ToUniversalTime();
    }

    public static Guid IdFor(DateTimeOffset slot) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"ClientRelationsDigest:{slot.UtcDateTime:O}")).AsSpan(0, 16));

    // The digest and consumed source notices commit together. A retry always uses
    // the saved snapshot and delivery ID, even after a restart or queue failure.
    public static async Task QueueAsync(RequestPortalDbContext db, DateTimeOffset now, CancellationToken token)
    {
        if (SlotFor(now) is not { } slot) return;
        Guid id = IdFor(slot);
        if (await db.OutboxMessages.AnyAsync(m => m.Id == id, token)) return;
        var sources = await db.OutboxMessages.Where(m => m.ProcessedUtc == null && !m.IsTerminal &&
            m.MessageType == typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName &&
            m.OccurredUtc <= slot).OrderBy(m => m.OccurredUtc).ToListAsync(token);
        if (sources.Count == 0) return;
        var items = sources.Select(m => JsonSerializer.Deserialize<QuickIntakeClientRelationsEmailRequestedMessage>(m.Payload)
            ?? throw new InvalidOperationException("Invalid Client Relations notice.")).ToArray();
        if (items.Any(m => string.IsNullOrWhiteSpace(m.FfpManagerReference) ||
            string.Equals(m.FfpManagerReference, m.PublicReference, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Client Relations digest requires FFP Manager references.");
        var digest = new ClientRelationsDigest(id, slot, items);
        db.OutboxMessages.Add(new OutboxMessage { Id = id, OccurredUtc = slot,
            MessageType = typeof(ClientRelationsDigest).FullName!, Payload = JsonSerializer.Serialize(digest) });
        foreach (var source in sources)
        {
            source.ProcessedUtc = now;
            source.NextAttemptUtc = null;
            source.LastError = null;
        }
        await db.SaveChangesAsync(token);
    }
}
