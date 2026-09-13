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
    Duplicate = 9
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
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        Id = id;
        RequestId = requestId;
        Description = description.Trim();
        Quantity = quantity;
        UnitOfMeasureId = unitOfMeasureId;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    public Guid UnitOfMeasureId { get; private set; }
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
