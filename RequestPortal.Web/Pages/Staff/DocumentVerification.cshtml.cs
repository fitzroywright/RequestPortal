using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Staff;

// Read-only queue. Verification/rejection/archiving remains in the existing
// Request and Organization review pages with their established checks/audits.
public sealed class DocumentVerificationModel(RequestPortalDbContext db) : PageModel
{
    private const int PageSize = 40;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Scope { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool PotentialTestOnly { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int TotalPending { get; private set; }
    public int MatchingCount { get; private set; }
    public int PotentialTestCount { get; private set; }
    public int TotalPages => Math.Max(1, (MatchingCount + PageSize - 1) / PageSize);
    public IReadOnlyList<PendingDocumentRow> Rows { get; private set; } = [];

    public sealed record PendingDocumentRow(
        Guid DocumentId, Guid OwnerId, DocumentOwnerType OwnerType,
        string FileName, DocumentType DocumentType, DateTimeOffset UploadedUtc,
        string OrganizationName, string? RequestReference,
        bool PotentialTest, bool OwnerMissing);

    public async Task<IActionResult> OnGetAsync()
    {
        if (Scope is not (null or "" or "Requests" or "Organizations"))
            return BadRequest("Invalid owner filter.");
        Search = Search?.Trim();
        if (Search?.Length > 150) return BadRequest("Search is too long.");

        var pending = db.Documents.AsNoTracking()
            .Where(d => d.VerificationStatus == DocumentVerificationStatus.Pending && !d.IsArchived);
        TotalPending = await pending.CountAsync(HttpContext.RequestAborted);

        var query =
            from d in pending
            join r in db.AssistanceRequests.AsNoTracking() on d.OwnerId equals r.Id into requests
            from r in requests.DefaultIfEmpty()
            join q in db.QuickIntakes.AsNoTracking() on d.OwnerId equals q.RequestId into intakes
            from q in intakes.DefaultIfEmpty()
            join o in db.Organizations.AsNoTracking() on d.OrganizationId equals o.Id into organizations
            from o in organizations.DefaultIfEmpty()
            select new
            {
                d.Id, d.OwnerId, d.OwnerType, d.DocumentType, d.OriginalFileName, d.UploadedUtc,
                OrganizationName = o == null ? "Organization unavailable" : o.Name,
                RequestReference = r == null ? null : r.PublicReference,
                OwnerMissing = d.OwnerType == DocumentOwnerType.AssistanceRequest
                    ? r == null : o == null || d.OwnerId != o.Id,
                PotentialTest =
                    (r != null && (r.PublicReference.StartsWith("RP-DEMO") ||
                        r.PublicReference == "RP-20260905-INFO01" ||
                        r.PublicReference == "RP-20260831-FULF01" ||
                        r.PublicReference == "RP-20260907-PROJ01" ||
                        r.PublicReference == "RP-20260903-APPR01" ||
                        r.PublicReference == "RP-20260909-DEMO01" ||
                        r.PublicReference == "RP-26-10020" ||
                        r.PublicReference == "RP-26-10021" ||
                        r.PublicReference == "RP-26-10022" ||
                        r.PublicReference == "RP-26-10023")) ||
                    (q != null && (q.FirstName.ToLower().StartsWith("smady") ||
                        q.LastName.ToLower().StartsWith("smady"))) ||
                    (o != null && (o.Name.ToLower().Contains("demo") ||
                        o.Name.ToLower().Contains("test"))) ||
                    d.OriginalFileName.ToLower().StartsWith("demo") ||
                    d.OriginalFileName.ToLower().StartsWith("test")
            };

        PotentialTestCount = await query.CountAsync(x => x.PotentialTest, HttpContext.RequestAborted);
        if (Scope == "Requests") query = query.Where(x => x.OwnerType == DocumentOwnerType.AssistanceRequest);
        else if (Scope == "Organizations") query = query.Where(x => x.OwnerType == DocumentOwnerType.Organization);
        if (PotentialTestOnly) query = query.Where(x => x.PotentialTest);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var pattern = "%" + Search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x =>
                EF.Functions.ILike(x.OriginalFileName, pattern, "\\") ||
                (x.RequestReference != null && EF.Functions.ILike(x.RequestReference, pattern, "\\")) ||
                EF.Functions.ILike(x.OrganizationName, pattern, "\\"));
        }
        MatchingCount = await query.CountAsync(HttpContext.RequestAborted);
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        var result = await query.OrderBy(x => x.UploadedUtc).ThenBy(x => x.Id)
            .Skip((PageNumber - 1) * PageSize).Take(PageSize)
            .ToListAsync(HttpContext.RequestAborted);
        Rows = result.Select(x => new PendingDocumentRow(
            x.Id, x.OwnerId, x.OwnerType, x.OriginalFileName, x.DocumentType,
            x.UploadedUtc, x.OrganizationName, x.RequestReference, x.PotentialTest, x.OwnerMissing)).ToArray();
        return Page();
    }
}
