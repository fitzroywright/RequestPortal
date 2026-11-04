using RequestPortal.Domain.Requests;

namespace RequestPortal.Application.Administration;

public sealed class StaffRequestSearchRow
{
    public Guid RequestId { get; init; }
    public string PublicReference { get; init; } = "";
    public string ApplicantName { get; init; } = "";
    public string RegisteredName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsUnverifiedQuickIntake { get; init; }
    public string OrganizationName { get; init; } = "";
    public string Category { get; init; } = "";
    public DateTimeOffset ReceivedUtc { get; init; }
    public RequestStatus Status { get; init; }
}

public sealed record StaffRequestSearchResult(IReadOnlyList<StaffRequestSearchRow> Items, int TotalCount, int PageNumber, int PageSize, Guid? ExactReferenceId)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public interface IStaffRequestSearchService
{
    Task<StaffRequestSearchResult> SearchAsync(string search, int pageNumber = 1, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, StaffRequestSearchRow>> GetApplicantsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken = default);
}
