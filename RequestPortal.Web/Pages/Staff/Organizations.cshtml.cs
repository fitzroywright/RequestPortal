using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OrganizationsModel : PageModel
{
    private readonly IStaffOrganizationDirectoryService organizationDirectoryService;

    public OrganizationsModel(IStaffOrganizationDirectoryService organizationDirectoryService)
    {
        this.organizationDirectoryService = organizationDirectoryService;
    }

    public IReadOnlyList<StaffOrganizationSummary> Organizations { get; private set; } = Array.Empty<StaffOrganizationSummary>();

    public async Task OnGetAsync()
    {
        Organizations = await organizationDirectoryService.GetAllOrganizationsAsync(HttpContext.RequestAborted);
    }
}
