using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Requests;

public sealed class RequestSubmissionPolicyOptions
{
    public const string SectionName = "RequestSubmissionPolicy";

    public bool Enabled { get; set; } = true;
    public int MaxActiveRequestsPerUser { get; set; } = 3;
    public int MaxSubmissionsPer24Hours { get; set; } = 2;
    public int MaxSubmissionsPer30Days { get; set; } = 5;
    public int MaxCancelledRequestsPer30Days { get; set; } = 3;
    public int DuplicateLookbackDays { get; set; } = 90;
}

public sealed class RequestSubmissionBlockedException : InvalidOperationException
{
    public RequestSubmissionBlockedException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IRequestSubmissionPolicy
{
    Task ValidateNewRequestAsync(
        Guid userId,
        Guid organizationId,
        RequestKind kind,
        string summary,
        CancellationToken cancellationToken = default);

    Task ValidateAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default);
}
