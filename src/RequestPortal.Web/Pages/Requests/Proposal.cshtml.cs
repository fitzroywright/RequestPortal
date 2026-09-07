using System;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class ProposalModel : PageModel
{
    private static readonly Regex htmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private readonly UserManager<ApplicationUser> userManager;
    private readonly IProjectProposalService proposalService;
    private readonly IPortalWorkflowService workflowService;

    public ProposalModel(
        UserManager<ApplicationUser> userManager,
        IProjectProposalService proposalService,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.proposalService = proposalService;
        this.workflowService = workflowService;
    }

    public ProjectProposalDetail? Proposal { get; private set; }

    [BindProperty]
    public ProposalInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        Proposal = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
        if (Proposal is null)
        {
            return NotFound();
        }

        Input.Title = Proposal.Title;
        Input.RichContentHtml = Proposal.RichContentHtml;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (!ModelState.IsValid)
        {
            Proposal = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
            return Page();
        }

        await proposalService.SaveAsync(user.Id, id, Input.Title, Input.RichContentHtml, HttpContext.RequestAborted);
        TempData["Success"] = "Project proposal saved.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (!ModelState.IsValid)
        {
            Proposal = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
            return Page();
        }

        string plainText = htmlTagRegex.Replace(Input.RichContentHtml ?? string.Empty, " ").Trim();
        if (plainText.Length < 100)
        {
            ModelState.AddModelError(nameof(Input.RichContentHtml), "A project proposal needs a substantive narrative before submission.");
            Proposal = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
            return Page();
        }

        await proposalService.SaveAsync(user.Id, id, Input.Title, Input.RichContentHtml, HttpContext.RequestAborted);
        await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Project proposal submitted to FFP for review.";
        return RedirectToPage("/Requests/Details", new { id });
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ProposalInput
    {
        [Required]
        [StringLength(500)]
        public string Title { get; set; } = "Project Proposal";

        [Required]
        public string RichContentHtml { get; set; } = string.Empty;
    }
}
