using System;
using System.Collections.Generic;

namespace RequestPortal.Domain.Requests;

public sealed class AssistanceRequest
{
    private readonly List<AssistanceRequestItem> items = new();
    private readonly List<RequestStatusHistory> statusHistory = new();

    private AssistanceRequest()
    {
    }

    public AssistanceRequest(Guid id, string publicReference, Guid organizationId, Guid createdByUserId, DateTimeOffset createdUtc)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization is required.", nameof(organizationId));
        }

        Id = id;
        PublicReference = publicReference;
        OrganizationId = organizationId;
        CreatedByUserId = createdByUserId;
        Status = RequestStatus.Draft;
        Priority = RequestPriority.Normal;
        CreatedUtc = createdUtc;
        statusHistory.Add(new RequestStatusHistory(Guid.NewGuid(), id, RequestStatus.Draft, createdByUserId, createdUtc, "Request created."));
    }

    public Guid Id { get; private set; }

    public string PublicReference { get; private set; } = string.Empty;

    public Guid OrganizationId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public Guid? SubmittedByUserId { get; private set; }

    public Guid? BeneficiaryId { get; private set; }

    public RequestStatus Status { get; private set; }

    public RequestPriority Priority { get; private set; }

    public string? Summary { get; private set; }

    public string? Category { get; private set; }

    public string? Subcategory { get; private set; }

    public string? ProgramCategory { get; private set; }

    public string? Location { get; private set; }

    public string? Circumstances { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? SubmittedUtc { get; private set; }

    public IReadOnlyCollection<AssistanceRequestItem> Items => items.AsReadOnly();

    public IReadOnlyCollection<RequestStatusHistory> StatusHistory => statusHistory.AsReadOnly();

    public void SetSummary(string summary)
    {
        EnsureDraft();
        Summary = summary.Trim();
    }

    public void SetDetails(
        Guid? beneficiaryId,
        string? category,
        string? subcategory,
        string? programCategory,
        RequestPriority priority,
        string? location,
        string? circumstances)
    {
        EnsureDraft();
        BeneficiaryId = beneficiaryId;
        Category = Normalize(category);
        Subcategory = Normalize(subcategory);
        ProgramCategory = Normalize(programCategory);
        Priority = priority;
        Location = Normalize(location);
        Circumstances = Normalize(circumstances);
    }

    public void AddItem(AssistanceRequestItem item)
    {
        EnsureDraft();
        items.Add(item);
    }

    public void Submit(Guid userId, DateTimeOffset submittedUtc)
    {
        EnsureDraft();

        if (items.Count == 0)
        {
            throw new InvalidOperationException("A request must contain at least one requested item before submission.");
        }

        SubmittedByUserId = userId;
        SubmittedUtc = submittedUtc;
        TransitionTo(RequestStatus.Submitted, userId, submittedUtc, "Request submitted for review.");
    }

    public void RequestMoreInformation(Guid staffUserId, DateTimeOffset changedUtc, string reason)
    {
        EnsureStatus(RequestStatus.Submitted);
        TransitionTo(RequestStatus.MoreInformationRequired, staffUserId, changedUtc, reason);
    }

    public void Resubmit(Guid userId, DateTimeOffset changedUtc)
    {
        EnsureStatus(RequestStatus.MoreInformationRequired);
        TransitionTo(RequestStatus.Submitted, userId, changedUtc, "Additional information supplied and request resubmitted.");
    }

    public void Approve(Guid staffUserId, DateTimeOffset changedUtc)
    {
        EnsureStatus(RequestStatus.Submitted);
        TransitionTo(RequestStatus.Approved, staffUserId, changedUtc, "Request approved.");
    }

    public void Reject(Guid staffUserId, DateTimeOffset changedUtc, string reason)
    {
        EnsureStatus(RequestStatus.Submitted);
        TransitionTo(RequestStatus.Rejected, staffUserId, changedUtc, reason);
    }

    public void Cancel(Guid userId, DateTimeOffset changedUtc, string reason)
    {
        if (Status != RequestStatus.Draft && Status != RequestStatus.Submitted && Status != RequestStatus.MoreInformationRequired)
        {
            throw new InvalidOperationException("This request cannot be cancelled from its current status.");
        }

        TransitionTo(RequestStatus.Cancelled, userId, changedUtc, reason);
    }

    public void MarkDuplicate(Guid staffUserId, DateTimeOffset changedUtc, string reason)
    {
        if (Status != RequestStatus.Submitted && Status != RequestStatus.MoreInformationRequired)
        {
            throw new InvalidOperationException("Only submitted requests can be marked as duplicates.");
        }

        TransitionTo(RequestStatus.Duplicate, staffUserId, changedUtc, reason);
    }

    public void MarkPendingFulfilment(Guid staffUserId, DateTimeOffset changedUtc)
    {
        EnsureStatus(RequestStatus.Approved);
        TransitionTo(RequestStatus.PendingFulfilment, staffUserId, changedUtc, "Request moved to fulfilment.");
    }

    public void MarkFulfilled(Guid staffUserId, DateTimeOffset changedUtc)
    {
        EnsureStatus(RequestStatus.PendingFulfilment);
        TransitionTo(RequestStatus.Fulfilled, staffUserId, changedUtc, "Request fulfilled.");
    }

    public void Close(Guid staffUserId, DateTimeOffset changedUtc)
    {
        EnsureStatus(RequestStatus.Fulfilled);
        TransitionTo(RequestStatus.Closed, staffUserId, changedUtc, "Request closed.");
    }

    private void TransitionTo(RequestStatus status, Guid changedByUserId, DateTimeOffset changedUtc, string? reason)
    {
        Status = status;
        statusHistory.Add(new RequestStatusHistory(Guid.NewGuid(), Id, status, changedByUserId, changedUtc, reason));
    }

    private void EnsureDraft()
    {
        EnsureStatus(RequestStatus.Draft);
    }

    private void EnsureStatus(RequestStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Request must be {expected} for this operation.");
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
