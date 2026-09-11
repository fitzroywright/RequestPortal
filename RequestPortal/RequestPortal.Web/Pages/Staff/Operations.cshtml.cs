using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OperationsModel : PageModel
{
    private readonly IStaffOperationsService staffOperationsService;

    public OperationsModel(IStaffOperationsService staffOperationsService)
    {
        this.staffOperationsService = staffOperationsService;
    }

    public StaffOperationsSnapshot Snapshot { get; private set; } = new(
        Array.Empty<PendingOrganizationWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<DocumentWorkItem>());

    public async Task OnGetAsync()
    {
        Snapshot = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);
    }
}
