using System;

namespace RequestPortal.Domain.Requests;

public enum RequestStatus
{
    Draft = 0,
    Submitted = 1,
    MoreInformationRequired = 2,
    Approved = 3,
    Rejected = 4,
    Cancelled = 5,
    PendingFulfilment = 6,
    Fulfilled = 7,
    Closed = 8,
    Duplicate = 9,
    RepresentativeReviewRequired = 10,
    OnHold = 11,
    Investigated = 12,
    SentForAssessment = 13
}

public enum RequestPriority
{
    Normal = 0,
    High = 1,
    Urgent = 2
}

public sealed class AssistanceRequestItem
{
    private AssistanceRequestItem()
    {
    }

    public AssistanceRequestItem(Guid id, Guid requestId, string description, decimal quantity, Guid unitOfMeasureId)
        : this(id, requestId, description, description, quantity, unitOfMeasureId, null)
    {
    }

    public AssistanceRequestItem(
        Guid id,
        Guid requestId,
        string itemName,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            throw new ArgumentException("Item or service name is required.", nameof(itemName));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description or specification is required.", nameof(description));
        }

        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        Id = id;
        RequestId = requestId;
        ItemName = itemName.Trim();
        Description = description.Trim();
        Quantity = quantity;
        UnitOfMeasureId = unitOfMeasureId;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public string ItemName { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public decimal Quantity { get; private set; }

    public Guid UnitOfMeasureId { get; private set; }

    public void Update(
        string itemName,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            throw new ArgumentException("Item or service name is required.", nameof(itemName));
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("Description or specification is required.", nameof(description));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");

        ItemName = itemName.Trim();
        Description = description.Trim();
        Quantity = quantity;
        UnitOfMeasureId = unitOfMeasureId;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }
}

public sealed class RequestStatusHistory
{
    private RequestStatusHistory()
    {
    }

    public RequestStatusHistory(Guid id, Guid requestId, RequestStatus status, Guid changedByUserId, DateTimeOffset changedUtc, string? reason)
    {
        Id = id;
        RequestId = requestId;
        Status = status;
        ChangedByUserId = changedByUserId;
        ChangedUtc = changedUtc;
        Reason = reason;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public RequestStatus Status { get; private set; }

    public Guid ChangedByUserId { get; private set; }

    public DateTimeOffset ChangedUtc { get; private set; }

    public string? Reason { get; private set; }
}
