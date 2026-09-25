using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Reviewers;

namespace RequestPortal.Web.Pages.ExternalReview;

public sealed class AuthenticateModel : PageModel
{
    private readonly IExternalReviewerService reviewerService;

    public AuthenticateModel(IExternalReviewerService reviewerService)
    {
        this.reviewerService = reviewerService;
    }

    public async Task<IActionResult> OnGetAsync(Guid id, string token)
    {
        ExternalReviewerAuthentication? reviewer =
            await reviewerService.AuthenticateAsync(id, token, HttpContext.RequestAborted);
        if (reviewer is null)
        {
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, reviewer.InvitationId.ToString("D")),
            new Claim(ClaimTypes.Name, reviewer.DisplayName),
            new Claim(ClaimTypes.Email, reviewer.Email),
            new Claim("requestportal.external-review", "true"),
            new Claim("requestportal.external-review.scope", reviewer.Scope)
        ];

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(claims, "ExternalReviewer"));

        await HttpContext.SignInAsync(
            "ExternalReviewer",
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                AllowRefresh = false,
                ExpiresUtc = reviewer.ExpiresUtc
            });

        return RedirectToPage("/ExternalReview/Index");
    }
}
