using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Messaging;

namespace RequestPortal.Web.Services.WhatsAppCommissioning;

public sealed record WhatsAppTestStatus(Guid Id, string State, DateTimeOffset AuthorizedUtc, DateTimeOffset ExpiresUtc,
    DateTimeOffset? AttemptedUtc, DateTimeOffset? CompletedUtc, int? HttpStatus, int? ErrorCode, int? ErrorSubcode,
    string? MessageIdFingerprint, IReadOnlyList<CommissioningDelivery> Delivery);

public sealed class CommissioningService(ICommissioningJournal journal, CommissioningGateway gateway,
    IOptions<NotificationOptions> options, IDataProtectionProvider protection, IIntegrationOperatorAuthorization authorization,
    TimeProvider time)
{
    public async Task<WhatsAppTestReadiness> InspectAsync(Guid actor, CancellationToken ct)
    {
        await authorization.DemandAsync(actor, ct);
        return await gateway.InspectAsync(options.Value, ct);
    }
    public async Task<Guid> AuthorizeAsync(Guid actor, string recipient, string repeatedRecipient,
        bool verifiedInMeta, bool explicitlyAuthorized, CancellationToken ct)
    {
        await authorization.DemandAsync(actor, ct);
        CommissioningGateway.RequireTestConfiguration(options.Value);
        if (!verifiedInMeta || !explicitlyAuthorized || recipient != repeatedRecipient ||
            !Regex.IsMatch(recipient ?? "", @"^\+[1-9][0-9]{6,14}$"))
            throw new InvalidOperationException("Enter the same E.164 test recipient twice and explicitly confirm verification and authorization.");
        if (!(await gateway.InspectAsync(options.Value, ct)).Ready)
            throw new InvalidOperationException("Meta sender, messaging permission and approved hello_world/en_US template must pass readiness first.");
        var id = Guid.NewGuid();
        var now = time.GetUtcNow();
        var encrypted = Protector(id).Protect(recipient!);
        await journal.CreateAsync(new(id, actor, encrypted, options.Value.WhatsAppGraphApiVersion, now, now.AddMinutes(10)), ct);
        return id;
    }
    public async Task SendAsync(Guid actor, Guid id, string confirmation, CancellationToken ct)
    {
        await authorization.DemandAsync(actor, ct);
        CommissioningGateway.RequireTestConfiguration(options.Value);
        if (confirmation != "SEND HELLO_WORLD") throw new InvalidOperationException("Explicit send confirmation is required.");
        var current = await journal.ReadAsync(id, actor, ct) ?? throw new InvalidOperationException("Test authorization was not found.");
        if (current.ApiVersion != options.Value.WhatsAppGraphApiVersion || current.ExpiresUtc <= time.GetUtcNow() || current.State != "Authorized")
            throw new InvalidOperationException("Authorization expired, changed or was already attempted; it cannot be resent.");
        if (!(await gateway.InspectAsync(options.Value, ct)).Ready) throw new InvalidOperationException("Meta readiness must pass before sending.");
        var claimed = await journal.ClaimAsync(id, actor, time.GetUtcNow(), ct);
        if (claimed is null) throw new InvalidOperationException("This authorization has already been attempted or expired.");
        MetaTestResult result;
        try
        {
            string recipient = Protector(id).Unprotect(claimed.EncryptedRecipient);
            result = await gateway.SendHelloWorldAsync(options.Value, recipient, id, ct);
        }
        catch (Exception) { result = new("NotSent"); }
        // Persist outcome even if the browser disconnects. Failure leaves Attempted,
        // which is deliberately never retryable. A timeout cannot prove non-delivery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await journal.CompleteAsync(claimed with { State = result.State, Result = result, CompletedUtc = time.GetUtcNow() }, timeout.Token);
    }
    public async Task<WhatsAppTestStatus?> StatusAsync(Guid actor, Guid id, CancellationToken ct)
    {
        await authorization.DemandAsync(actor, ct);
        var test = await journal.ReadAsync(id, actor, ct);
        if (test is null) return null;
        var delivery = test.Result?.MessageId is { } messageId ? await journal.DeliveryAsync(messageId, ct) : [];
        return new(test.Id, test.State, test.AuthorizedUtc, test.ExpiresUtc, test.AttemptedUtc, test.CompletedUtc,
            test.Result?.HttpStatus, test.Result?.ErrorCode, test.Result?.ErrorSubcode,
            test.Result?.MessageId is { } idValue ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idValue))).ToLowerInvariant() : null, delivery);
    }
    private IDataProtector Protector(Guid id) => protection.CreateProtector("RequestPortal.WhatsAppCommissioning.v1", id.ToString("D"));
}
