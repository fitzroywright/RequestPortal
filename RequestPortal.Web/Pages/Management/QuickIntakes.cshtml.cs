using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;
using RequestPortal.Application.Administration;
namespace RequestPortal.Web.Pages.Management;
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class QuickIntakesModel(IQuickIntakeDashboardService service, IStaffRequestSearchService searchService) : PageModel
{
    [BindProperty(SupportsGet = true)] public DateOnly From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly Through { get; set; }
    [BindProperty(SupportsGet = true)] public string? Parish { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<QuickIntakeRecordLink> Records { get; private set; } = [];
    public IReadOnlyDictionary<Guid, StaffRequestSearchRow> RequestDetails { get; private set; } = new Dictionary<Guid, StaffRequestSearchRow>();
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid || From == default || Through == default || PageNumber < 1 || PageNumber > 10000) return BadRequest("Invalid filter.");
        try
        {
            Records = await service.GetRecordsAsync(new(From,Through),Parish,PageNumber,ct);
            if (Records.Count > 0)
                RequestDetails = await searchService.GetApplicantsAsync(Records.Select(record => record.RequestId).ToArray(), ct);
            return Page();
        }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }
}
