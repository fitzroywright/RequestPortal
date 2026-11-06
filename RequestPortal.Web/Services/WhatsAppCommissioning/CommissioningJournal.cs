using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Auditing;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services.WhatsAppCommissioning;

public sealed record CommissioningTest(Guid Id, Guid ActorId, string EncryptedRecipient, string ApiVersion,
    DateTimeOffset AuthorizedUtc, DateTimeOffset ExpiresUtc, string State = "Authorized",
    DateTimeOffset? AttemptedUtc = null, DateTimeOffset? CompletedUtc = null, MetaTestResult? Result = null);
public sealed record CommissioningDelivery(string State, DateTimeOffset? RecordedUtc, IReadOnlyList<int> ErrorCodes);
public interface ICommissioningJournal
{
    Task CreateAsync(CommissioningTest test, CancellationToken ct);
    Task<CommissioningTest?> ReadAsync(Guid id, Guid actor, CancellationToken ct);
    Task<CommissioningTest?> ClaimAsync(Guid id, Guid actor, DateTimeOffset now, CancellationToken ct);
    Task CompleteAsync(CommissioningTest test, CancellationToken ct);
    Task<IReadOnlyList<CommissioningDelivery>> DeliveryAsync(string messageId, CancellationToken ct);
}

// Processed audit receipts in the existing journal, never work for OutboxDispatcher.
// No new schema and no background delivery/retry path.
public sealed class CommissioningJournal(RequestPortalDbContext db) : ICommissioningJournal
{
    public const string MessageType = "RequestPortal.WhatsAppCommissioning.v1";
    public async Task CreateAsync(CommissioningTest test, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.OutboxMessages.Add(new() { Id = test.Id, MessageType = MessageType, Payload = JsonSerializer.Serialize(test),
            OccurredUtc = test.AuthorizedUtc, ProcessedUtc = test.AuthorizedUtc, IsTerminal = true });
        Audit(test, "WhatsAppTestRecipientAuthorized");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<CommissioningTest?> ReadAsync(Guid id, Guid actor, CancellationToken ct)
    {
        var row = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.MessageType == MessageType, ct);
        if (row is null) return null;
        var test = JsonSerializer.Deserialize<CommissioningTest>(row.Payload);
        return test?.ActorId == actor ? test : null;
    }
    public async Task<CommissioningTest?> ClaimAsync(Guid id, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Cross-process compare-and-set commits BEFORE any external request. A crash or
        // ambiguous response can never cause a retry of this authorization.
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET "Payload" = "Payload" || jsonb_build_object('State', 'Attempted',
                'AttemptedUtc', {now}::timestamptz), "AttemptCount"=1
            WHERE "Id"={id} AND "MessageType"={MessageType}
              AND "Payload"->>'ActorId'={actor.ToString()}
              AND "Payload"->>'State'='Authorized'
              AND ("Payload"->>'ExpiresUtc')::timestamptz > {now}
              AND "ProcessedUtc" IS NOT NULL AND "IsTerminal"=true
            """, ct);
        if (changed != 1) { await tx.RollbackAsync(ct); return null; }
        var test = await ReadAsync(id, actor, ct) ?? throw new InvalidOperationException("Commissioning journal is inconsistent.");
        Audit(test, "WhatsAppTestSendAttempted");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return test;
    }
    public async Task CompleteAsync(CommissioningTest test, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE outbox_messages SET "Payload"=CAST({JsonSerializer.Serialize(test)} AS jsonb)
            WHERE "Id"={test.Id} AND "MessageType"={MessageType} AND "Payload"->>'State'='Attempted'
              AND "Payload"->>'ActorId'={test.ActorId.ToString()}
            """, ct);
        if (changed != 1) throw new InvalidOperationException("Commissioning journal completion is inconsistent.");
        Audit(test, "WhatsAppTestSendRecorded");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<CommissioningDelivery>> DeliveryAsync(string messageId, CancellationToken ct)
    {
        // Existing signature-verified inbound status receipts; never change the webhook.
        var rows = await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT o.* FROM outbox_messages o WHERE o."MessageType"={WhatsAppWebhookStore.Consumer}
              AND o."ProcessedUtc" IS NOT NULL AND o."Payload"->>'Kind'='status'
              AND o."Payload"->>'MessageId'={messageId}
              AND o."Payload"->>'AccountId'={CommissioningGateway.Account}
              AND o."Payload"->>'PhoneNumberId'={CommissioningGateway.Phone}
              AND EXISTS (SELECT 1 FROM inbox_messages i WHERE i."MessageId"=o."Id"
                AND i."Consumer"={WhatsAppWebhookStore.Consumer} AND i."ProcessedUtc" IS NOT NULL)
            ORDER BY o."OccurredUtc" LIMIT 100
            """).AsNoTracking().ToListAsync(ct);
        return rows.Select(row => {
            var value = JsonSerializer.Deserialize<WhatsAppWebhookEvent>(row.Payload) ?? throw new InvalidOperationException();
            return new CommissioningDelivery(value.State is "sent" or "delivered" or "read" or "failed" ? value.State : "unknown", row.ProcessedUtc, value.ErrorCodes);
        }).ToList();
    }
    private void Audit(CommissioningTest test, string action) => db.AuditEntries.Add(new AuditEntry(
        Guid.NewGuid(), test.ActorId, action, "WhatsAppCommissioning", test.Id, null,
        DateTimeOffset.UtcNow, detail: test.State));
}
