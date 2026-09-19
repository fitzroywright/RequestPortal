namespace RequestPortal.Application.Portal;

public interface IRequestPortalOperationsFlowPublisher
{
    bool TryEnqueue(
        Guid requestId,
        string stage,
        DateTimeOffset observedAtUtc,
        string? detail = null,
        bool failed = false);
}
