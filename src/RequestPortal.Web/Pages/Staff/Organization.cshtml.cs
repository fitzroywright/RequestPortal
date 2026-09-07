using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OrganizationModel : PageModel
{
    private readonly IStaffOrganizationService organizationService;

    public OrganizationModel(IStaffOrganizationService organizationService)
    {
        this.organizationService = organizationService;
    }

    public StaffOrganizationDetail? Organization { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Organization = await organizationService.GetAsync(id, HttpContext.RequestAborted);
        if (Organization is null)
        {
            return NotFound();
        }

        return Page();
    }
}
