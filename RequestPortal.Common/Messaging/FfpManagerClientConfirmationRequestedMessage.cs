using System;
using System.Security.Cryptography;
using System.Text;

namespace RequestPortal.Common.Messaging;

public sealed record FfpManagerClientConfirmationRequestedMessage(
    Guid MessageId, DateTimeOffset OccurredUtc, Guid RequestId, string ApplicationNumber) : IMessage
{
    public static Guid IdForRequest(Guid requestId) => new(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId:D}|FfpManagerClientConfirmation")).AsSpan(0, 16));
}
