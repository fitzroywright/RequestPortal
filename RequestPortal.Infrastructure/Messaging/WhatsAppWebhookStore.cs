using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messaging;

// Deliberately excludes message text, contacts, phone numbers and provider error descriptions.
// Inbound events are receipts only: no identity linking, business mutations or automatic replies.
public sealed record WhatsAppWebhookEvent(string AccountId, string PhoneNumberId, string MessageId,
    string Kind, string State, string Timestamp, IReadOnlyList<int> ErrorCodes)
{
    public Guid ReceiptId => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { AccountId, PhoneNumberId, MessageId, Kind,
            Kind == "inbound" ? "" : State, Kind == "inbound" ? "" : Timestamp })))[..16]);
}

public interface IWhatsAppWebhookStore
{
    Task AcceptAsync(IReadOnlyList<WhatsAppWebhookEvent> events, CancellationToken cancellationToken);
}

public sealed class WhatsAppWebhookStore(RequestPortalDbContext db, IDiagnosticOperationsRouter diagnostics)
    : IWhatsAppWebhookStore
{
    public const string Consumer = "RequestPortal.WhatsAppWebhook.v1";

    public async Task AcceptAsync(IReadOnlyList<WhatsAppWebhookEvent> events, CancellationToken cancellationToken)
    {
        var failures = new List<WhatsAppWebhookEvent>();
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            foreach (var item in events)
            {
                var id = item.ReceiptId;
                var now = DateTimeOffset.UtcNow;
                // The composite inbox key arbitrates concurrent retries, including different app nodes.
                int inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inbox_messages ("MessageId", "Consumer", "ReceivedUtc", "ProcessedUtc")
                    VALUES ({id}, {Consumer}, {now}, {now}) ON CONFLICT DO NOTHING
                    """, cancellationToken);
                if (inserted == 0) continue;

                string payload = JsonSerializer.Serialize(item);
                // This is a fully processed receipt, not an outbound delivery work item. Keeping it
                // in the existing durable journal avoids involving outbound workers or a new queue.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO outbox_messages ("Id", "MessageType", "Payload", "OccurredUtc", "ProcessedUtc", "AttemptCount", "IsTerminal")
                    VALUES ({id}, {Consumer}, CAST({payload} AS jsonb), {now}, {now}, 0, false)
                    """, cancellationToken);
                if (item.State == "failed" || item.ErrorCodes.Count > 0) failures.Add(item);
            }
            await transaction.CommitAsync(cancellationToken);
        }

        if (failures.Count > 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
                await diagnostics.RouteAsync(new(DateTimeOffset.UtcNow, "RequestPortal.Messaging.WhatsApp",
                    "RP-WHATSAPP-DELIVERY-FAILED", DiagnosticSeverity.Error,
                    "Meta reported a WhatsApp message failure. Consult the protected webhook receipt journal.",
                    Properties: new Dictionary<string, string> { ["ErrorCodes"] = string.Join(",", failures.SelectMany(item => item.ErrorCodes).Distinct()) }), timeout.Token);
            }
            catch
            {
                // The failure remains durably recorded even when telemetry is unavailable.
            }
        }
    }
}
