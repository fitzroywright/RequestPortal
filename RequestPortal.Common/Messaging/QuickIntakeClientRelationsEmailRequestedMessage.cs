using System;
using System.Security.Cryptography;
using System.Text;

namespace RequestPortal.Common.Messaging;

public sealed record QuickIntakeClientRelationsEmailRequestedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    string PublicReference,
    string? FfpManagerReference = null) : IMessage
{
    // This durable prerequisite state is excluded from dispatch until integration
    // supplies the reference. Waiting for FFP Manager does not consume email retries.
    public static readonly string AwaitingReferenceMessageType =
        typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName + ".AwaitingFfpManagerReference";

    public static Guid IdForRequest(Guid requestId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"QuickIntakeClientRelationsEmail:{requestId:D}")).AsSpan(0, 16));
}
