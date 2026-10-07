using System;

namespace RequestPortal.Domain.Requests;

public sealed class QuickIntakeRecord
{
    private QuickIntakeRecord() { }

    public QuickIntakeRecord(
        Guid id,
        Guid requestId,
        Guid anonymousUserId,
        Guid individualContextId,
        string firstName,
        string lastName,
        string mobileNumber,
        string parish,
        string? town,
        DateTimeOffset createdUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Quick intake id is required.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));
        if (anonymousUserId == Guid.Empty) throw new ArgumentException("Anonymous user id is required.", nameof(anonymousUserId));
        if (individualContextId == Guid.Empty) throw new ArgumentException("Individual context id is required.", nameof(individualContextId));
        if (string.IsNullOrWhiteSpace(firstName)) throw new ArgumentException("First name is required.", nameof(firstName));
        if (string.IsNullOrWhiteSpace(lastName)) throw new ArgumentException("Last name is required.", nameof(lastName));
        if (string.IsNullOrWhiteSpace(mobileNumber)) throw new ArgumentException("Mobile number is required.", nameof(mobileNumber));
        if (string.IsNullOrWhiteSpace(parish)) throw new ArgumentException("Parish is required.", nameof(parish));

        Id = id;
        RequestId = requestId;
        AnonymousUserId = anonymousUserId;
        IndividualContextId = individualContextId;
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        MobileNumber = mobileNumber.Trim();
        Parish = parish.Trim();
        Town = Normalize(town);
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid AnonymousUserId { get; private set; }
    public Guid IndividualContextId { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string MobileNumber { get; private set; } = string.Empty;
    public string Parish { get; private set; } = string.Empty;
    public string? Town { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public Guid? ConvertedToUserId { get; private set; }
    public Guid? ConvertedByStaffUserId { get; private set; }
    public DateTimeOffset? ConvertedUtc { get; private set; }

    public bool IsConverted => ConvertedToUserId.HasValue;

    public void MarkConverted(Guid userId, Guid staffUserId, DateTimeOffset convertedUtc)
    {
        if (IsConverted) throw new InvalidOperationException("Quick intake has already been converted.");
        ConvertedToUserId = userId;
        ConvertedByStaffUserId = staffUserId;
        ConvertedUtc = convertedUtc;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
