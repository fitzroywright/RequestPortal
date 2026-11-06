using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using RequestPortal.Application.Auditing;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Pages.Account;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class DefaultPageRoutingTests
{
    private sealed class Audit : IBusinessAuditWriter
    {
        public int Calls;
        public Task WriteAsync(Guid actorUserId,string action,string entityType,Guid entityId,Guid? organizationId,string? detail=null,CancellationToken cancellationToken=default)
        { Calls++;return Task.CompletedTask; }
    }
    private static PageContext Context(bool staff,bool access=true,bool authenticated=true)
    {
        var claims=new List<Claim> {new(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString()),new(ClaimTypes.GivenName,"Applicant")};
        if(staff)claims.Add(new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue));
        if(access)claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        // Management permission must not change the default route.
        claims.Add(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ViewManagementDashboard));
        return new PageContext {HttpContext=new DefaultHttpContext {User=new(new ClaimsIdentity(claims,authenticated?"fixture":null))}};
    }
    [Theory]
    [InlineData(null,null)]
    [InlineData("/Management","/Management")]
    [InlineData("/Staff/Review/123?tab=documents","/Staff/Review/123?tab=documents")]
    [InlineData("https://example.invalid/",null)]
    [InlineData("//example.invalid/",null)]
    public async Task StaffCompletionUsesRequestsUnlessAnExplicitLocalReturnUrlExists(string? input,string? local)
    {
        var audit=new Audit();var page=new StaffCompleteModel(audit) {PageContext=Context(true)};
        page.Url=new UrlHelper(new ActionContext(page.HttpContext,new RouteData(),new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
        var result=await page.OnGetAsync(input);
        if(local is null)Assert.Equal("/Staff/Index",Assert.IsType<RedirectToPageResult>(result).PageName);
        else Assert.Equal(local,Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(1,audit.Calls);
    }
    [Fact]
    public async Task PendingStaffStillCannotEnterRequests()
    {
        var audit=new Audit();var page=new StaffCompleteModel(audit) {PageContext=Context(true,false)};
        Assert.Equal("/Account/StaffAccessDenied",Assert.IsType<RedirectToPageResult>(await page.OnGetAsync()).PageName);Assert.Equal(0,audit.Calls);
    }
    [Theory]
    [InlineData(true,true,true)]
    [InlineData(false,true,false)]
    [InlineData(false,false,false)]
    public void RootChangesOnlyForAuthenticatedStaff(bool staff,bool authenticated,bool redirects)
    {
        var page=new RequestPortal.Web.Pages.IndexModel {PageContext=Context(staff,authenticated:authenticated)};
        var result=page.OnGet();
        if(redirects)Assert.Equal("/Staff/Index",Assert.IsType<RedirectToPageResult>(result).PageName);
        else Assert.IsType<PageResult>(result);
    }
}
