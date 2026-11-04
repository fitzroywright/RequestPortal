using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

/// <summary>Staff-only, paged projections. Never joins memberships or loads request bodies.</summary>
public sealed class StaffRequestSearchService(RequestPortalDbContext db) : IStaffRequestSearchService
{
    private static int ValidPageSize(int size) => size is 25 or 50 or 100 ? size : 25;

    private IQueryable<StaffRequestSearchRow> Rows() =>
        from request in db.AssistanceRequests.AsNoTracking()
        join organization in db.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
        join intake in db.QuickIntakes.AsNoTracking() on request.Id equals intake.RequestId into intakes
        from intake in intakes.DefaultIfEmpty()
        join user in db.Users.AsNoTracking() on (request.SubmittedByUserId ?? request.CreatedByUserId) equals user.Id into users
        from user in users.DefaultIfEmpty()
        select new StaffRequestSearchRow
        {
            RequestId = request.Id,
            PublicReference = request.PublicReference,
            ApplicantName = intake != null ? (intake.FirstName + " " + intake.LastName).Trim()
                : user != null && ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim() != ""
                    ? ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim()
                    : user != null && user.DisplayName != "" ? user.DisplayName : "Name not provided",
            RegisteredName = user == null || (intake != null && intake.ConvertedToUserId == null) ? "" : ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim(),
            DisplayName = user == null || (intake != null && intake.ConvertedToUserId == null) ? "" : user.DisplayName,
            IsUnverifiedQuickIntake = intake != null && intake.ConvertedToUserId == null,
            OrganizationName = organization.Name,
            Category = request.Category ?? "Not provided",
            Summary = request.Summary ?? "",
            CreatedUtc = request.CreatedUtc,
            ReceivedUtc = request.SubmittedUtc ?? request.CreatedUtc,
            Status = request.Status
        };

    public Task<StaffRequestSearchResult> SearchAsync(string search, int pageNumber = 1, CancellationToken cancellationToken = default) =>
        SearchAsync(search, pageNumber, 25, cancellationToken);

    public Task<StaffRequestSearchResult> GetSubmittedQueueAsync(int pageNumber = 1, int pageSize = 25, CancellationToken cancellationToken = default) =>
        PageAsync(Rows().Where(r => r.Status == RequestPortal.Domain.Requests.RequestStatus.Submitted), pageNumber, pageSize, cancellationToken);

    public async Task<StaffRequestSearchResult> SearchAsync(string search, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        pageSize = ValidPageSize(pageSize);
        string term = string.Join(" ", search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (term.Length == 0 || term.Length > 200)
            return new([], 0, 1, pageSize, null);

        // Escape LIKE metacharacters: user input is a literal, parameterized search.
        string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        string reference = Escape(term);
        var exact = await db.AssistanceRequests.AsNoTracking()
            .Where(r => EF.Functions.ILike(r.PublicReference, reference, "\\"))
            .Select(r => r.Id).Take(2).ToListAsync(cancellationToken);
        if (exact.Count == 1) return new([], 1, 1, pageSize, exact[0]);

        var query = Rows();
        if (exact.Count > 1) query = query.Where(r => EF.Functions.ILike(r.PublicReference, reference, "\\"));
        else foreach (string token in term.Split(' '))
        {
            string pattern = "%" + Escape(token) + "%";
            query = query.Where(r => EF.Functions.ILike(r.ApplicantName, pattern, "\\") ||
                EF.Functions.ILike(r.RegisteredName, pattern, "\\") || EF.Functions.ILike(r.DisplayName, pattern, "\\"));
        }
        return await PageAsync(query, pageNumber, pageSize, cancellationToken);
    }

    private static async Task<StaffRequestSearchResult> PageAsync(IQueryable<StaffRequestSearchRow> query, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        pageSize = ValidPageSize(pageSize);
        int total = await query.CountAsync(cancellationToken);
        int page = Math.Clamp(pageNumber, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var items = await query.OrderByDescending(r => r.ReceivedUtc).ThenBy(r => r.PublicReference).ThenBy(r => r.RequestId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(items, total, page, pageSize, null);
    }

    public async Task<IReadOnlyDictionary<Guid, StaffRequestSearchRow>> GetApplicantsAsync(IReadOnlyCollection<Guid> requestIds, CancellationToken cancellationToken = default)
    {
        return await Rows().Where(r => requestIds.Contains(r.RequestId)).ToDictionaryAsync(r => r.RequestId, cancellationToken);
    }
}
