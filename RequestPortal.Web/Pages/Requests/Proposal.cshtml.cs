using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
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
    private readonly IProjectPlanningService planningService;
    private readonly IPortalWorkflowService workflowService;

    public ProposalModel(
        UserManager<ApplicationUser> userManager,
        IProjectProposalService proposalService,
        IProjectPlanningService planningService,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.proposalService = proposalService;
        this.planningService = planningService;
        this.workflowService = workflowService;
    }

    public ProjectProposalDetail? Proposal { get; private set; }
    public ProjectPlanningDetail? Planning { get; private set; }
    public IReadOnlyList<ProjectProposalRevisionDetail> Revisions { get; private set; } = [];
    public IReadOnlyList<LongFormTemplate> Templates { get; private set; } = [];

    [BindProperty]
    public ProposalInput Input { get; set; } = new();

    [BindProperty]
    public BudgetInput Budget { get; set; } = new();

    [BindProperty]
    public MilestoneInput Milestone { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await LoadAsync(user, id);
        if (Proposal is null) return NotFound();
        Input.Title = Proposal.Title;
        Input.RichContentHtml = Proposal.RichContentHtml;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (!ModelState.IsValid)
        {
            await LoadAsync(user, id);
            return Page();
        }

        await proposalService.SaveAsync(user.Id, id, Input.Title, Input.RichContentHtml ?? string.Empty, HttpContext.RequestAborted);
        TempData["Success"] = "Project proposal saved.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAutosaveAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(Input.Title)) Input.Title = "Project Proposal";
        await proposalService.SaveAutosaveAsync(user.Id, id, Input.Title, Input.RichContentHtml ?? string.Empty, HttpContext.RequestAborted);
        ProjectProposalDetail? current = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
        return new JsonResult(new { saved = true, version = current?.Version, savedUtc = current?.UpdatedUtc });
    }

    public async Task<IActionResult> OnPostTemplateAsync(Guid id, string templateKey)
    {
        ApplicationUser user = await GetUserAsync();
        await proposalService.ApplyTemplateAsync(user.Id, id, templateKey, HttpContext.RequestAborted);
        TempData["Success"] = "Document template applied.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRestoreRevisionAsync(Guid id, Guid revisionId)
    {
        ApplicationUser user = await GetUserAsync();
        await proposalService.RestoreRevisionAsync(user.Id, id, revisionId, HttpContext.RequestAborted);
        TempData["Success"] = "Earlier document revision restored.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetDocxAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        ProjectProposalExport export = await proposalService.ExportDocxAsync(user.Id, id, false, HttpContext.RequestAborted);
        return File(export.Content, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnGetPdfAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        ProjectProposalExport export = await proposalService.ExportPdfAsync(user.Id, id, false, HttpContext.RequestAborted);
        return File(export.Content, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnPostAddBudgetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await planningService.AddBudgetItemAsync(user.Id, id, Budget.Description, Budget.Quantity, Budget.UnitCost,
            Budget.FfpRequested, Budget.OrganizationContribution, Budget.OtherFunding, HttpContext.RequestAborted);
        TempData["Success"] = "Budget item added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveBudgetAsync(Guid id, Guid itemId)
    {
        ApplicationUser user = await GetUserAsync();
        await planningService.RemoveBudgetItemAsync(user.Id, id, itemId, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddMilestoneAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await planningService.AddMilestoneAsync(user.Id, id, Milestone.Title, Milestone.ExpectedOutcome, Milestone.TargetDate, HttpContext.RequestAborted);
        TempData["Success"] = "Milestone added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMilestoneAsync(Guid id, Guid milestoneId)
    {
        ApplicationUser user = await GetUserAsync();
        await planningService.RemoveMilestoneAsync(user.Id, id, milestoneId, HttpContext.RequestAborted);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        string richContentHtml = Input.RichContentHtml ?? string.Empty;
        string plainText = htmlTagRegex.Replace(richContentHtml, " ").Trim();
        if (plainText.Length < 100)
        {
            ModelState.AddModelError(nameof(Input.RichContentHtml), "A project proposal needs a substantive narrative before submission.");
            await LoadAsync(user, id);
            return Page();
        }

        ProjectPlanningDetail planning = await planningService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
        if (planning.Budget.Count == 0 || planning.Milestones.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Add at least one structured budget item and one milestone before submitting a project.");
            await LoadAsync(user, id);
            return Page();
        }

        await proposalService.SaveAsync(user.Id, id, Input.Title, richContentHtml, HttpContext.RequestAborted);
        await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Project proposal submitted to FFP for review.";
        return RedirectToPage("/Requests/Details", new { id });
    }

    private async Task LoadAsync(ApplicationUser user, Guid id)
    {
        Proposal = await proposalService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
        Templates = proposalService.GetTemplates("Project");
        if (Proposal is not null)
        {
            Planning = await planningService.GetAsync(user.Id, id, false, HttpContext.RequestAborted);
            Revisions = await proposalService.GetRevisionsAsync(user.Id, id, false, HttpContext.RequestAborted);
        }
    }

    private async Task<ApplicationUser> GetUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

    public sealed class ProposalInput
    {
        [Required, StringLength(500)]
        public string Title { get; set; } = "Project Proposal";
        [Required]
        public string RichContentHtml { get; set; } = string.Empty;
    }

    public sealed class BudgetInput
    {
        [Required, StringLength(1000)] public string Description { get; set; } = string.Empty;
        [Range(typeof(decimal), "0.001", "999999999")] public decimal Quantity { get; set; } = 1;
        [Range(typeof(decimal), "0", "999999999")] public decimal UnitCost { get; set; }
        [Range(typeof(decimal), "0", "999999999")] public decimal FfpRequested { get; set; }
        [Range(typeof(decimal), "0", "999999999")] public decimal OrganizationContribution { get; set; }
        [Range(typeof(decimal), "0", "999999999")] public decimal OtherFunding { get; set; }
    }

    public sealed class MilestoneInput
    {
        [Required, StringLength(500)] public string Title { get; set; } = string.Empty;
        [StringLength(4000)] public string ExpectedOutcome { get; set; } = string.Empty;
        [Required] public DateOnly TargetDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddMonths(1));
    }
}
