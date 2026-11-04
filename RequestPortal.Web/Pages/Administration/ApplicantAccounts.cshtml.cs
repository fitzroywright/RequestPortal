using Common.Security.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Administration;

[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class ApplicantAccountsModel(ApplicantIdentityAdministration service) : PageModel
{
    [BindProperty(SupportsGet=true)] public string? Search { get; set; }
    [BindProperty(SupportsGet=true)] public string? Status { get; set; }
    [BindProperty(SupportsGet=true)] public int PageNumber { get; set; }=1;
    [BindProperty(SupportsGet=true)] public Guid? Id { get; set; }
    [BindProperty] public Guid RecoveryId { get; set; }
    [BindProperty] public string Provider { get; set; }="Google";
    [BindProperty] public string Reason { get; set; }="";
    [BindProperty] public string Verification { get; set; }="";
    [BindProperty] public bool IndependentlyVerified { get; set; }
    public ApplicantAccounts Results { get; private set; }=new([],0,1,25);
    public ApplicantAccountDetails? Selected { get; private set; }
    public string[] Providers { get; private set; }=[];
    public string? InvitationUrl { get; private set; }
    public async Task<IActionResult> OnGetAsync() => await Handle(async()=>await Load());
    public async Task<IActionResult> OnPostInitiateAsync() => await Handle(async()=>
    {
        if(!Id.HasValue) return BadRequest();
        await service.InitiateAsync(User,Id.Value,Provider,Reason,HttpContext.RequestAborted);
        TempData["Success"]="Recovery initiated and audited. A different administrator must independently verify applicant ownership and approve it.";
        return RedirectToPage(new { Id });
    });
    public async Task<IActionResult> OnPostApproveAsync() => await Handle(async()=>
    {
        if(!Id.HasValue) return BadRequest();
        string token=await service.ApproveAsync(User,Id.Value,RecoveryId,Verification,IndependentlyVerified,HttpContext.RequestAborted);
        InvitationUrl=Url.Page("/Account/IdentityRecovery",null,null,null,null,"token="+token);
        return await Load(); // Invitation is returned once, never stored in TempData, cookies, logs, or the database.
    });
    public async Task<IActionResult> OnPostCancelAsync() => await Handle(async()=>
    {
        if(!Id.HasValue) return BadRequest();
        await service.CancelAsync(User,Id.Value,RecoveryId,Reason,HttpContext.RequestAborted);
        TempData["Success"]="Recovery cancelled and audited.";return RedirectToPage(new { Id });
    });
    private async Task<IActionResult> Load()
    {
        Providers=await service.ProvidersAsync();
        if(Id.HasValue) { Selected=await service.DetailsAsync(User,Id.Value,HttpContext.RequestAborted); return Selected is null?NotFound():Page(); }
        Results=await service.SearchAsync(User,Search,Status,PageNumber,HttpContext.RequestAborted);return Page();
    }
    private async Task<IActionResult> Handle(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl="no-store";Response.Headers["Referrer-Policy"]="no-referrer";
        try { return await action(); }
        catch(UnauthorizedAccessException) { return Forbid(StaffOidcOptions.StaffCookieScheme); }
        catch(FreshAdministratorAuthenticationRequiredException) { return RedirectToPage("/Account/StaffLogin",new { returnUrl=Url.Page("ApplicantAccounts",new { Id }) }); }
        catch(InvalidOperationException error) { ModelState.AddModelError("",error.Message);return await Load(); }
    }
}
