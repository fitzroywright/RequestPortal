using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class DetailsModel : PageModel
{
    private static readonly Guid eachUnitId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public DetailsModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public RequestDetail? Request { get; private set; }

    [BindProperty]
    public ItemInput Item { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        Request = await workflowService.GetRequestAsync(user.Id, id, isStaff: false, HttpContext.RequestAborted);

        if (Request is null)
        {
            return NotFound();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddItemAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            Request = await workflowService.GetRequestAsync(user.Id, id, isStaff: false, HttpContext.RequestAborted);
            return Page();
        }

        await workflowService.AddRequestItemAsync(
            user.Id,
            id,
            Item.Description,
            Item.Quantity,
            eachUnitId,
            HttpContext.RequestAborted);

        TempData["Success"] = "Requested item added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request submitted to FFP for review.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.ResubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request resubmitted to FFP.";
        return RedirectToPage(new { id });
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ItemInput
    {
        [Required]
        [StringLength(1000, MinimumLength = 2)]
        public string Description { get; set; } = string.Empty;

        [Range(typeof(decimal), "0.001", "999999999999")]
        public decimal Quantity { get; set; } = 1;
    }
}
