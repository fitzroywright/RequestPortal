using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Integration;
using RequestPortal.Domain.Integration;

namespace RequestPortal.Infrastructure.Integration;

public sealed partial class IntegrationStatusService
{
    public async Task<ApplicationFieldPreview> PreviewFieldsAsync(Guid staffUserId, Guid requestId, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        var (link, source) = await FieldUpdateSourceAsync(requestId, cancellationToken);
        var snapshot = await bridgeClient.ReadFieldsAsync(requestId, cancellationToken);
        VerifyFieldIdentity(link, snapshot, source.PublicReference);
        var preview = new ApplicationFieldPreview(requestId, source.PublicReference, snapshot, QuickIntakeApplicationFields.MappedValues(source));
        await auditWriter.WriteAsync(staffUserId, "IntegrationFieldsPreviewed", "IntegrationLink", link.Id, null,
            $"application={snapshot.Identity.ApplicationId}; differingFields={string.Join(",", preview.Differences.Where(d => d.Different).Select(d => d.Name))}", cancellationToken);
        return preview;
    }

    public async Task ApplyFieldsAsync(Guid staffUserId, ApplicationFieldPreview preview, IReadOnlyList<string> approved, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        preview.ValidateApproval(approved);
        await using var requestLock = await IntegrationRequestLock.AcquireAsync(dbContext, preview.RequestId, cancellationToken);
        var (link, source) = await FieldUpdateSourceAsync(preview.RequestId, cancellationToken);
        VerifyFieldIdentity(link, preview.Destination, source.PublicReference);
        if (QuickIntakeApplicationFields.MappedValues(source) != preview.Proposed)
            throw new InvalidOperationException("Quick Intake source values changed after preview. Generate a new preview.");
        // A durable intent entry precedes the external transaction. A transport failure is
        // unconfirmed; no automatic resend or retry occurs for historical updates.
        string detail = $"application={preview.Destination.Identity.ApplicationId}; approvedFields={string.Join(",", approved)}";
        await auditWriter.WriteAsync(staffUserId, "IntegrationFieldsUpdateRequested", "IntegrationLink", link.Id, null, detail, cancellationToken);
        try
        {
            var readback = await bridgeClient.UpdateFieldsAsync(new(source, preview.Destination, approved), cancellationToken);
            preview.ValidateReadback(readback, approved);
            await auditWriter.WriteAsync(staffUserId, "IntegrationFieldsUpdatedVerified", "IntegrationLink", link.Id, null, detail, cancellationToken);
        }
        catch (Exception)
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await auditWriter.WriteAsync(staffUserId, "IntegrationFieldsUpdateUnconfirmed", "IntegrationLink", link.Id, null, detail, auditTimeout.Token);
            throw;
        }
    }

    private async Task<(IntegrationLink Link, ExternalApplicationSubmission Source)> FieldUpdateSourceAsync(Guid requestId, CancellationToken token)
    {
        var row = await GetTransferAsync(requestId, token);
        if (row?.CanUpdateFields != true) throw new InvalidOperationException("Field updates require a verified Quick Intake destination in Synchronized or Reconciliation Required state.");
        var link = await GetLinkAsync(row.LinkId!.Value, token);
        EnsureSupported(link);
        var intake = await dbContext.QuickIntakes.AsNoTracking().SingleAsync(q => q.RequestId == requestId, token);
        var source = new ExternalApplicationSubmission(link.Id, requestId, row.PublicReference, Guid.Empty, "Quick Intake", null, null, null, null,
            Guid.Empty, intake.CreatedUtc, "Individual", "Normal", null, null, null, null, null, null, [], [], DocumentSource: "Request Portal - Quick Intake");
        return (link, QuickIntakeSubmissionMapping.Apply(source, intake));
    }

    private static void VerifyFieldIdentity(IntegrationLink link, ApplicationFieldSnapshot snapshot, string publicReference)
    {
        FfpManagerIntegrationService.ValidateReceipt(link.RequestId, snapshot.Identity);
        if (snapshot.Identity.ApplicationId != link.ExternalReference || snapshot.Identity.ApplicationNumber != link.ApplicationNumber ||
            string.Equals(snapshot.Identity.ApplicationNumber, publicReference, StringComparison.OrdinalIgnoreCase))
            throw new IntegrationTransferException(TransferFailures.For("DestinationVerification", "DESTINATION_CONFLICT"));
    }
}
