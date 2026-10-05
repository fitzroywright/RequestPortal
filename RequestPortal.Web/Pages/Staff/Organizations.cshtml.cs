using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Portal;
using PortalStaffOrganizationSummary = RequestPortal.Application.Portal.StaffOrganizationSummary;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OrganizationsModel : PageModel
{
    private readonly IStaffOrganizationDirectoryService organizationDirectoryService;
    private readonly IStaffOperationsService staffOperationsService;

    public OrganizationsModel(
        IStaffOrganizationDirectoryService organizationDirectoryService,
        IStaffOperationsService staffOperationsService)
    {
        this.organizationDirectoryService = organizationDirectoryService;
        this.staffOperationsService = staffOperationsService;
    }

    public IReadOnlyList<PortalStaffOrganizationSummary> Organizations { get; private set; } = Array.Empty<PortalStaffOrganizationSummary>();
    public IReadOnlyList<OrganizationReverificationWorkItem> ReverificationOrganizations { get; private set; } = Array.Empty<OrganizationReverificationWorkItem>();
    public IReadOnlyList<DocumentWorkItem> DocumentsAwaitingVerification { get; private set; } = Array.Empty<DocumentWorkItem>();

    public async Task OnGetAsync()
    {
        Organizations = await organizationDirectoryService.GetAllOrganizationsAsync(HttpContext.RequestAborted);
        StaffOperationsSnapshot snapshot = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);
        ReverificationOrganizations = snapshot.ReverificationOrganizations;
        DocumentsAwaitingVerification = snapshot.DocumentsAwaitingVerification;
    }
}
