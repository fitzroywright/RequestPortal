using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Hosting;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;
using ApplicationUser=RequestPortal.Infrastructure.Identity.ApplicationUser;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ApplicantIdentityRecoveryTests
{
    private static CancellationToken Ct=>TestContext.Current.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required PostgreSqlTestDatabase Database;
        public required RequestPortalAccounts Accounts;
        public required ApplicantIdentityAdministration Service;
        public required RequestPortalAuthorizationStore Roles;
        public required Clock Time;
        public required SecurityAccount First,Second,Reviewer;
        public required ApplicationUser Applicant;
        public ClaimsPrincipal Actor(SecurityAccount account,bool fresh=true,bool surface=true)=>new(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier,account.Id),new Claim(AccountSecurityHosting.AuthenticationTimeClaim,(fresh?Time.Now:Time.Now.AddHours(-1)).ToUnixTimeSeconds().ToString()),
            new Claim(StaffOidcOptions.StaffSurfaceClaim,surface?StaffOidcOptions.StaffSurfaceValue:"public")},"fixture"));
        public async ValueTask DisposeAsync()=>await Database.DisposeAsync();
    }
    private static async Task<Fixture> Setup(bool enrolled=false)
    {
        var database=await PostgreSqlTestDatabase.CreateAsync(Ct);var db=database.Db;var accounts=new RequestPortalAccounts(db.Database.GetConnectionString()!);accounts.EnsureSchema();
        var roles=new RequestPortalAuthorizationStore(db,Options.Create(new ActiveDirectoryOptions()));await roles.EnsureInitializedAsync(Ct);
        SecurityAccount Staff(string alias,string role)
        {
            var proof=new VerifiedIdentity(new("staff-oidc","fixture-tenant",alias),AuthenticationProviderKind.EntraId,alias,alias,DateTimeOffset.UtcNow,"oidc");
            var account=accounts.Staff.Resolve(proof,AccountPopulation.Staff,true).Account;accounts.Staff.SetStatus(account.Id,SecurityAccountStatus.Active,"fixture");
            var roleId=db.Roles.Single(r=>r.Name==role).Id;
            db.UserRoles.Add(new IdentityUserRole<Guid>{UserId=Guid.Parse(account.Id),RoleId=roleId});db.SaveChanges();return accounts.Staff.Find(account.Id)!;
        }
        var first=Staff("First administrator",PortalRoles.Administrator);var second=Staff("Second administrator",PortalRoles.Administrator);var reviewer=Staff("Ordinary reviewer",PortalRoles.Reviewer);
        var applicant=new ApplicationUser{Id=Guid.NewGuid(),UserName="existing@example.test",NormalizedUserName="EXISTING@EXAMPLE.TEST",Email="existing@example.test",NormalizedEmail="EXISTING@EXAMPLE.TEST",FirstName="Alex",LastName="Brown",DisplayName="Alex Brown",CreatedUtc=DateTimeOffset.UtcNow,SecurityStamp=Guid.NewGuid().ToString("N"),ConcurrencyStamp=Guid.NewGuid().ToString("N")};
        db.Users.Add(applicant);await db.SaveChangesAsync(Ct);
        if(enrolled) accounts.Public.Create(new(applicant.Id.ToString(),applicant.UserName,applicant.DisplayName,AuthenticationProviderKind.External,AccountPopulation.PublicApplicant,SecurityAccountStatus.Active,Proof("Microsoft","old-identity").Key),first.Id);
        var options=new AuthenticationOptions();options.AddScheme("Google",s=>s.HandlerType=typeof(CookieAuthenticationHandler));options.AddScheme("Microsoft",s=>s.HandlerType=typeof(CookieAuthenticationHandler));
        var clock=new Clock();var service=new ApplicantIdentityAdministration(accounts,db,new PermissionAuthorizer(roles),new AuthenticationSchemeProvider(Options.Create(options)),clock);
        return new(){Database=database,Accounts=accounts,Roles=roles,Service=service,Time=clock,First=first,Second=second,Reviewer=reviewer,Applicant=applicant};
    }
    private static VerifiedIdentity Proof(string provider="Google",string subject="new-verified-subject",string email="different-contact@example.test")=>new(new(provider,PublicExternalIdentity.Scope(provider),subject),AuthenticationProviderKind.External,"verified-provider-login","Verified provider",DateTimeOffset.UtcNow,"oauth",claims:new Dictionary<string,string>{{"contact_email",email}});
    private static async Task<string> Approve(Fixture f)
    {
        Guid recovery=await f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Google","Verified support case fixture",Ct);
        return await f.Service.ApproveAsync(f.Actor(f.Second),f.Applicant.Id,recovery,"Independent in-person ownership verification: CASE-TEST",true,Ct);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task VerifiedRecoveryPreservesAccountIdRelationshipsAndExistingBindingsAndIsSingleUse(bool enrolled)
    {
        await using var f=await Setup(enrolled);var db=f.Database.Db;
        var org=new Organization(Guid.NewGuid(),"Fixture organization","JM",f.Applicant.Id,DateTimeOffset.UtcNow);db.Organizations.Add(org);
        var membership=new OrganizationMembership(Guid.NewGuid(),org.Id,f.Applicant.Id,DateTimeOffset.UtcNow);membership.Approve(Guid.Parse(f.First.Id),DateTimeOffset.UtcNow);db.OrganizationMemberships.Add(membership);
        var request=new AssistanceRequest(Guid.NewGuid(),"REQ-RECOVERY",org.Id,f.Applicant.Id,DateTimeOffset.UtcNow);db.AssistanceRequests.Add(request);await db.SaveChangesAsync(Ct);
        string token=await Approve(f);var invitation=await f.Service.InvitationAsync(token,Ct);Assert.NotNull(invitation);
        var recovered=await f.Service.CompleteAsync(token,Proof(),Ct);Assert.Equal(f.Applicant.Id,recovered.Id);
        Assert.Equal(f.Applicant.Id.ToString(),f.Accounts.Public.Resolve(Proof(),AccountPopulation.PublicApplicant,true).Account.Id);
        Assert.Equal(enrolled?2:1,f.Accounts.Public.GetIdentities(recovered.Id.ToString()).Count);
        if(enrolled) Assert.Equal(recovered.Id.ToString(),f.Accounts.Public.Resolve(Proof("Microsoft","old-identity"),AccountPopulation.PublicApplicant,true).Account.Id);
        Assert.Null(await f.Service.InvitationAsync(token,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteAsync(token,Proof(subject:"different-subject"),Ct));
        Assert.Equal(f.Applicant.Id,(await db.AssistanceRequests.SingleAsync(Ct)).CreatedByUserId);Assert.Equal(f.Applicant.Id,(await db.OrganizationMemberships.SingleAsync(Ct)).UserId);
        Assert.Equal(4,await db.Users.CountAsync(Ct));
        var detail=await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct);Assert.Equal(1,detail!.RequestCount);Assert.Single(detail.Memberships);
        Assert.Equal(new[]{"ApplicantIdentityRecovered","RecoveryInvitationConsumed","OwnershipVerifiedAndRecoveryApproved","RecoveryInitiated"}.Order(),detail.Events.Select(e=>e.Action).Order());
        Assert.Single(await db.OutboxMessages.Where(m=>m.MessageType.Contains("ApplicantIdentityRecoveredMessage")).ToListAsync(Ct));
        string storage=await db.Database.SqlQueryRaw<string>("SELECT string_agg(coalesce(token_hash,'') || reason || coalesce(verification_reference,''),' ') AS \"Value\" FROM requestportal_identity_recoveries").SingleAsync(Ct);
        Assert.DoesNotContain(token,storage);Assert.DoesNotContain("new-verified-subject",storage);
    }
    [Fact]
    public async Task SearchIsPagedServerSideByNamesEmailReferenceAndLifecycleWithoutIncludingStaff()
    {
        await using var f=await Setup();var db=f.Database.Db;
        var org=new Organization(Guid.NewGuid(),"Fixture organization","JM",f.Applicant.Id,DateTimeOffset.UtcNow);db.Organizations.Add(org);db.AssistanceRequests.Add(new(Guid.NewGuid(),"REQ-ACCOUNT-10452",org.Id,f.Applicant.Id,DateTimeOffset.UtcNow));
        for(int i=0;i<27;i++)db.Users.Add(new ApplicationUser{Id=Guid.NewGuid(),UserName="duplicate-"+i,FirstName="Alex",LastName="Brown",DisplayName="Alex Brown",CreatedUtc=DateTimeOffset.UtcNow});await db.SaveChangesAsync(Ct);
        foreach(var term in new[]{"Alex","Brown","alex brown","lex bro"}) { var result=await f.Service.SearchAsync(f.Actor(f.First),term,null,1,Ct);Assert.Equal(28,result.Total);Assert.Equal(25,result.Items.Count);var next=await f.Service.SearchAsync(f.Actor(f.First),term,null,2,Ct);Assert.Equal(3,next.Items.Count);Assert.Empty(result.Items.Select(x=>x.Id).Intersect(next.Items.Select(x=>x.Id))); }
        foreach(var term in new[]{"EXISTING@example.test","req-account-10452"}) Assert.Equal(f.Applicant.Id,Assert.Single((await f.Service.SearchAsync(f.Actor(f.First),term,null,1,Ct)).Items).Id);
        Assert.Equal(28,(await f.Service.SearchAsync(f.Actor(f.First),null,"Enrollment required",1,Ct)).Total);
        Assert.Empty((await f.Service.SearchAsync(f.Actor(f.First),"absent",null,1,Ct)).Items);
        Assert.Empty((await f.Service.SearchAsync(f.Actor(f.First),null,"Disabled",1,Ct)).Items);
    }
    [Fact]
    public async Task ReviewerPublicPendingAndStaleSessionsCannotPerformRecoveryAndApprovalCannotBeSelfApprovedOrEmailOnly()
    {
        await using var f=await Setup();
        foreach(var actor in new[]{new ClaimsPrincipal(),f.Actor(f.Reviewer),f.Actor(f.First,surface:false)})
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.SearchAsync(actor,null,null,1,Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.InitiateAsync(actor,f.Applicant.Id,"Google","Support case fixture",Ct));
        }
        await Assert.ThrowsAsync<FreshAdministratorAuthenticationRequiredException>(()=>f.Service.InitiateAsync(f.Actor(f.First,false),f.Applicant.Id,"Google","Support case fixture",Ct));
        Guid id=await f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Google","Support case fixture",Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.Actor(f.First),f.Applicant.Id,id,"Verified independent ownership fixture",true,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ApproveAsync(f.Actor(f.Second),f.Applicant.Id,id,"Email matches existing@example.test",false,Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ApproveAsync(f.Actor(f.Reviewer),f.Applicant.Id,id,"Verified independent ownership fixture",true,Ct));
        f.Accounts.Staff.SetStatus(f.Second.Id,SecurityAccountStatus.Suspended,f.First.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ApproveAsync(f.Actor(f.Second),f.Applicant.Id,id,"Verified independent ownership fixture",true,Ct));
        Assert.Empty(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
    }
    [Fact]
    public async Task ExpiredCancelledAndWrongProviderInvitationsCannotLink()
    {
        await using var f=await Setup();string token=await Approve(f);var invitation=(await f.Service.InvitationAsync(token,Ct))!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteAsync(token,Proof("Microsoft"),Ct));Assert.NotNull(await f.Service.InvitationAsync(token,Ct));
        await f.Service.CancelAsync(f.Actor(f.First),f.Applicant.Id,invitation.RecoveryId,"Applicant withdrew recovery request",Ct);Assert.Null(await f.Service.InvitationAsync(token,Ct));
        string expired=await Approve(f);f.Time.Now=f.Time.Now.AddMinutes(31);Assert.Null(await f.Service.InvitationAsync(expired,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteAsync(expired,Proof(),Ct));Assert.Empty(f.Accounts.Public.List());
    }
    [Fact]
    public async Task AnIdentityAlreadyBoundElsewhereCannotBeStolenEvenWhenEmailsMatch()
    {
        await using var f=await Setup();var proof=Proof(email:f.Applicant.Email!);
        var other=f.Accounts.Public.Resolve(Proof(email:"other@example.test"),AccountPopulation.PublicApplicant,true).Account;
        string token=await Approve(f);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.CompleteAsync(token,proof,Ct));
        Assert.Equal(other.Id,f.Accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true).Account.Id);
        Assert.Null(f.Accounts.Public.Find(f.Applicant.Id.ToString()));Assert.Null(await f.Service.InvitationAsync(token,Ct));
        Assert.Equal("Failed",Assert.Single((await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Recoveries).Status);
    }
    [Fact]
    public async Task InactiveApplicantsAndUnconfiguredProvidersCannotBeRecovered()
    {
        await using var f=await Setup(true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Facebook","Support case fixture",Ct));
        f.Accounts.Public.SetStatus(f.Applicant.Id.ToString(),SecurityAccountStatus.Disabled,f.First.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Google","Support case fixture",Ct));
    }
    private sealed class StaffAuthentication(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,Microsoft.Extensions.Logging.ILoggerFactory logger,System.Text.Encodings.Web.UrlEncoder encoder)
        : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
        {
            var f=Context.RequestServices.GetRequiredService<Fixture>();string actor=Request.Headers["X-Actor"].ToString();
            if(actor=="anonymous")return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.NoResult());
            var account=actor=="Second"?f.Second:actor=="Reviewer"?f.Reviewer:f.First;
            var principal=f.Actor(account);var identity=(ClaimsIdentity)principal.Identity!;
            identity.AddClaim(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
            if(actor!="Reviewer")identity.AddClaim(new(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
            return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(new(principal,Scheme.Name)));
        }
    }
    private sealed class OAuthBackchannel : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"synthetic-test-token\",\"token_type\":\"Bearer\",\"expires_in\":3600}",System.Text.Encoding.UTF8,"application/json")});
    }
    private sealed class WebFixture : IAsyncDisposable
    {
        public required Microsoft.AspNetCore.Builder.WebApplication App;
        public required HttpClient Client;
        private readonly Dictionary<string,string> cookies=[];
        public async ValueTask DisposeAsync(){Client.Dispose();await App.DisposeAsync();}
        public async Task<HttpResponseMessage> Send(string url,string actor="First",Dictionary<string,string>? fields=null)
        {
            using var request=new HttpRequestMessage(fields is null?HttpMethod.Get:HttpMethod.Post,url);
            request.Headers.Add("X-Actor",actor);if(cookies.Count>0)request.Headers.Add("Cookie",string.Join("; ",cookies.Select(p=>p.Key+"="+p.Value)));
            if(fields is not null)request.Content=new FormUrlEncodedContent(fields);
            var response=await Client.SendAsync(request,Ct);
            if(response.Headers.TryGetValues("Set-Cookie",out var values))foreach(var value in values){var pair=value.Split(';',2)[0].Split('=',2);cookies[pair[0]]=pair[1];}
            return response;
        }
    }
    private static async Task<WebFixture> Web(Fixture f)
    {
        f.Database.Db.ChangeTracker.Clear();
        var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions{ApplicationName=typeof(RequestPortal.Web.Pages.Account.IdentityRecoveryModel).Assembly.GetName().Name,ContentRootPath=Path.GetFullPath("../../../../../RequestPortal.Web",AppContext.BaseDirectory),WebRootPath="wwwroot",EnvironmentName="Test"});
        builder.WebHost.UseTestServer();builder.Logging.ClearProviders();
        builder.Services.AddSingleton<RequestPortal.Application.Reporting.IStaffReportingService>(new RequestPortal.Infrastructure.Reporting.StaffReportingService(f.Database.Db));
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<RequestPortal.Application.Auditing.IBusinessAuditWriter>((_,_)=>Task.CompletedTask));
        builder.Services.AddSingleton(f);builder.Services.AddSingleton(f.Service);builder.Services.AddSingleton(f.Accounts);builder.Services.AddSingleton(f.Database.Db);
        builder.Services.AddSingleton<RequestPortal.Common.Time.IClock,RequestPortal.Common.Time.SystemClock>();
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<global::Common.Diagnostics.IDiagnosticOperationsRouter>((_,_)=>Task.CompletedTask));
        builder.Services.AddSingleton(MessageNavigationPageTests.Proxy.For<RequestPortal.Application.Notifications.INotificationPreferenceService>((_,_)=>Task.CompletedTask));
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<RequestPortalDbContext>().AddSignInManager();
        builder.Services.AddCommonAuthenticationRateLimiting();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme,o=>o.Events.OnValidatePrincipal=c=>AccountSecurityHosting.ValidateCookieAsync(c,f.Accounts.Public))
            .AddCookie(IdentityConstants.ExternalScheme)
            .AddScheme<AuthenticationSchemeOptions,StaffAuthentication>(StaffOidcOptions.StaffCookieScheme,_=>{})
            .AddScheme<AuthenticationSchemeOptions,StaffAuthentication>("ExternalReviewer",_=>{})
            .AddOAuth("Google",o=>{
                o.ClientId="synthetic-fixture";o.ClientSecret="synthetic-fixture";o.AuthorizationEndpoint="https://oauth.example.test/authorize";o.TokenEndpoint="https://oauth.example.test/token";
                o.CallbackPath="/signin-google";o.SignInScheme=IdentityConstants.ExternalScheme;o.Backchannel=new HttpClient(new OAuthBackchannel());
                o.Events.OnCreatingTicket=c=>{var identity=(ClaimsIdentity)c.Principal!.Identity!;identity.AddClaim(new(ClaimTypes.NameIdentifier,"controlled-google-subject"));identity.AddClaim(new(ClaimTypes.Email,"unrelated-email@example.test"));identity.AddClaim(new(ClaimTypes.Name,"Controlled Google identity"));return Task.CompletedTask;};
            });
        var assembly=typeof(RequestPortal.Web.Pages.Account.IdentityRecoveryModel).Assembly;
        assembly.GetType("RequestPortal.Web.Startup.AuthorizationServiceRegistration")!.GetMethod("Add")!.Invoke(null,[builder]);
        builder.Services.AddPageAuthorizationFixture();
        var app=builder.Build();assembly.GetType("RequestPortal.Web.Startup.ApplicationPipeline")!.GetMethod("Configure")!.Invoke(null,[app]);app.MapRazorPages();await app.StartAsync(Ct);
        var client=app.GetTestServer().CreateClient();client.BaseAddress=new("https://localhost");return new(){App=app,Client=client};
    }
    private static string Antiforgery(string html)=>System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static void Snapshot(string name,string html)
    {
        string? directory=Environment.GetEnvironmentVariable("REQUESTPORTAL_IDENTITY_RENDER_OUTPUT");if(string.IsNullOrWhiteSpace(directory))return;Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,name+".html"),html);
    }
    [Fact]
    public async Task ActualAdministratorPagesIssueInvitationAndCorrelatedOAuthRecoversTheOriginalAccountThenNormalGoogleSignInWorks()
    {
        await using var f=await Setup();Assert.False(string.IsNullOrWhiteSpace(f.Applicant.SecurityStamp));await using var web=await Web(f);string path="/Administration/ApplicantAccounts?Id="+f.Applicant.Id;
        using var first=await web.Send(path);Assert.Equal(System.Net.HttpStatusCode.OK,first.StatusCode);string html=await first.Content.ReadAsStringAsync(Ct);Snapshot("identity-details",html);Assert.DoesNotContain("subject_id",html);
        using var initiated=await web.Send(path+"&handler=Initiate","First",new(){["__RequestVerificationToken"]=Antiforgery(html),["Id"]=f.Applicant.Id.ToString(),["Provider"]="Google",["Reason"]="Support CASE-HTTP independently verified"});Assert.Equal(System.Net.HttpStatusCode.Redirect,initiated.StatusCode);
        using var second=await web.Send(path,"Second");html=await second.Content.ReadAsStringAsync(Ct);var recovery=Assert.Single((await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Recoveries);
        using var approved=await web.Send(path+"&handler=Approve","Second",new(){["__RequestVerificationToken"]=Antiforgery(html),["Id"]=f.Applicant.Id.ToString(),["RecoveryId"]=recovery.Id.ToString(),["Verification"]="Independent in-person ownership verification CASE-HTTP",["IndependentlyVerified"]="true"});
        Assert.Equal(System.Net.HttpStatusCode.OK,approved.StatusCode);Assert.Contains("no-store",approved.Headers.CacheControl!.ToString());html=await approved.Content.ReadAsStringAsync(Ct);
        string invitation=System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html,"id=\"recovery-invitation\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);Assert.StartsWith("/Account/IdentityRecovery#token=",invitation);string token=invitation.Split("#token=")[1];
        using var landing=await web.Send("/Account/IdentityRecovery","anonymous");html=await landing.Content.ReadAsStringAsync(Ct);Snapshot("recovery",html);
        using var csrf=await web.Send("/Account/IdentityRecovery","anonymous",new(){["Token"]=token,["__RequestVerificationToken"]="invalid"});Assert.Equal(System.Net.HttpStatusCode.BadRequest,csrf.StatusCode);
        using var challenge=await web.Send("/Account/IdentityRecovery","anonymous",new(){["Token"]=token,["__RequestVerificationToken"]=Antiforgery(html)});Assert.Equal(System.Net.HttpStatusCode.Redirect,challenge.StatusCode);Assert.DoesNotContain(token,challenge.Headers.Location!.OriginalString);
        string state=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(challenge.Headers.Location.Query)["state"].ToString();
        using var oauth=await web.Send("/signin-google?code=fixture-code&state="+Uri.EscapeDataString(state),"anonymous");Assert.Equal(System.Net.HttpStatusCode.Redirect,oauth.StatusCode);
        using var completed=await web.Send(oauth.Headers.Location!.OriginalString,"anonymous");Assert.True(completed.StatusCode==System.Net.HttpStatusCode.Redirect,"Callback returned "+completed.StatusCode+"; safe diagnostic="+System.Text.RegularExpressions.Regex.Match(await completed.Content.ReadAsStringAsync(Ct),"RP-RECOVERY-[A-Z]+").Value+"; recovery state="+string.Join(",",(await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Recoveries.Select(r=>r.Status)));Assert.StartsWith("/Dashboard",completed.Headers.Location!.OriginalString);
        var proof=Proof(subject:"controlled-google-subject");Assert.Equal(f.Applicant.Id.ToString(),f.Accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true).Account.Id);Assert.Null(await f.Service.InvitationAsync(token,Ct));
        using var linked=await web.Send("/Account/LinkedIdentities","anonymous");Assert.Equal(System.Net.HttpStatusCode.OK,linked.StatusCode);string linkedHtml=await linked.Content.ReadAsStringAsync(Ct);Assert.Contains("Google",linkedHtml);
        using var finalMethod=await web.Send("/Account/LinkedIdentities?handler=Unlink","anonymous",new(){["__RequestVerificationToken"]=Antiforgery(linkedHtml),["providerId"]="Google",["scopeId"]=PublicExternalIdentity.Scope("Google"),["subjectId"]="controlled-google-subject"});Assert.Equal(System.Net.HttpStatusCode.BadRequest,finalMethod.StatusCode);Assert.Single(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
        // The normal login path now resolves the same immutable provider binding, without a recovery token.
        using var loginPage=await web.Send("/Account/IdentityRecovery","anonymous");string loginToken=Antiforgery(await loginPage.Content.ReadAsStringAsync(Ct));
        using var normal=await web.Send("/Account/Login?handler=External","anonymous",new(){["__RequestVerificationToken"]=loginToken,["provider"]="Google",["returnUrl"]="/Account/LinkedIdentities"});Assert.Equal(System.Net.HttpStatusCode.Redirect,normal.StatusCode);
        string normalState=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(normal.Headers.Location!.Query)["state"].ToString();
        using var normalOAuth=await web.Send("/signin-google?code=fixture-code&state="+Uri.EscapeDataString(normalState),"anonymous");
        using var normalCallback=await web.Send(normalOAuth.Headers.Location!.OriginalString,"anonymous");Assert.Equal(System.Net.HttpStatusCode.Redirect,normalCallback.StatusCode);Assert.Equal("/Account/LinkedIdentities",normalCallback.Headers.Location!.OriginalString);
        Assert.Equal(4,await f.Database.Db.Users.CountAsync(Ct));Assert.Single(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
    }
    [Fact]
    public async Task ActualIdentityPagesDenyReviewersAndPublicUsersAndRejectUncorrelatedCallbacks()
    {
        await using var f=await Setup();await using var web=await Web(f);
        using var reviewer=await web.Send("/Administration/ApplicantAccounts","Reviewer");Assert.Equal(System.Net.HttpStatusCode.Forbidden,reviewer.StatusCode);
        using var anonymous=await web.Send("/Administration/ApplicantAccounts","anonymous");Assert.Contains(anonymous.StatusCode,new[]{System.Net.HttpStatusCode.Unauthorized,System.Net.HttpStatusCode.Redirect});
        using var callback=await web.Send("/Account/IdentityRecovery?handler=Callback","anonymous");Assert.Equal(System.Net.HttpStatusCode.BadRequest,callback.StatusCode);
        Assert.Empty(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
        using var directory=await web.Send("/Administration/ApplicantAccounts");Assert.Equal(System.Net.HttpStatusCode.OK,directory.StatusCode);Snapshot("identity-search",await directory.Content.ReadAsStringAsync(Ct));
    }
    [Fact]
    public async Task FlatReportsKeepAllFiltersPaginationOpeningAndActualExportFormats()
    {
        await using var f=await Setup();var db=f.Database.Db;var now=DateTimeOffset.UtcNow;
        var org=new Organization(Guid.NewGuid(),"Reporting fixture organization","JM",f.Applicant.Id,now.AddDays(-2));db.Organizations.Add(org);
        AssistanceRequest Request(string reference,string summary,bool submit=false)
        {
            var request=new AssistanceRequest(Guid.NewGuid(),reference,org.Id,f.Applicant.Id,now.AddDays(-1));request.SetKind(RequestKind.Project);request.SetSummary(summary);request.SetDetails(null,"Food",null,"Recorded program",RequestPriority.High,"Kingston",null);if(submit)request.Submit(f.Applicant.Id,now);db.AssistanceRequests.Add(request);return request;
        }
        var match=Request("REQ-FILTER-MATCH","Filter fixture exact",true);
        for(int i=0;i<102;i++)Request("REQ-PAGED-"+i.ToString("D3"),"Pagination fixture");
        await db.SaveChangesAsync(Ct);await using var web=await Web(f);
        string query="Search=Filter%20fixture&Status=Submitted&CountryCode=JM&Category=Food&Priority=High&FromDate="+now.AddDays(-2).ToString("yyyy-MM-dd")+"&ToDate="+now.ToString("yyyy-MM-dd");
        using var page=await web.Send("/Staff/Reports?"+query);Assert.Equal(System.Net.HttpStatusCode.OK,page.StatusCode);string html=await page.Content.ReadAsStringAsync(Ct);Snapshot("reports",html);
        Assert.Contains("REQ-FILTER-MATCH",html);Assert.DoesNotContain("REQ-PAGED-",html);Assert.DoesNotContain("class=\"card",html);Assert.Contains("reports-summary",html);Assert.Contains("/Staff/Review/"+match.Id,html);
        foreach(string field in new[]{"Search","Status","CountryCode","Category","Priority","FromDate","ToDate"})Assert.Contains("name=\""+field+"\"",html);
        foreach(string handler in new[]{"Export","ExportXlsx","Pdf","Print"})
        {
            var link=System.Text.RegularExpressions.Regex.Matches(html,"<a[^>]*href=\"([^\"]+)\"[^>]*>").Cast<System.Text.RegularExpressions.Match>().Select(m=>System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).Single(url=>url.Contains("handler="+handler+"&")||url.EndsWith("handler="+handler));
            foreach(string filter in new[]{"Search=Filter","Status=Submitted","CountryCode=JM","Category=Food","Priority=High","FromDate=","ToDate="})Assert.Contains(filter,link);
            using var export=await web.Send(link);Assert.Equal(System.Net.HttpStatusCode.OK,export.StatusCode);var bytes=await export.Content.ReadAsByteArrayAsync(Ct);
            if(handler=="ExportXlsx")Assert.Equal("PK",System.Text.Encoding.ASCII.GetString(bytes,0,2));
            else if(handler=="Pdf")Assert.StartsWith("%PDF",System.Text.Encoding.ASCII.GetString(bytes,0,4));
            else { string content=System.Text.Encoding.UTF8.GetString(bytes);Assert.Contains("REQ-FILTER-MATCH",content);Assert.DoesNotContain("REQ-PAGED-",content); }
        }
        using var paged=await web.Send("/Staff/Reports?Search=Pagination&PageNumber=2");html=await paged.Content.ReadAsStringAsync(Ct);Snapshot("reports-paged",html);Assert.Contains("Page 2 of 2",html);Assert.Equal(2,System.Text.RegularExpressions.Regex.Matches(html,"<td><strong>REQ-PAGED-").Count);Assert.Contains("Search=Pagination",html);
        using var clear=await web.Send("/Staff/Reports");string cleared=await clear.Content.ReadAsStringAsync(Ct);Assert.Contains("103 matching requests",cleared);Snapshot("reports-clear",cleared);
        using var empty=await web.Send("/Staff/Reports?CountryCode=US");html=await empty.Content.ReadAsStringAsync(Ct);Assert.Contains("No requests match",html);Snapshot("reports-empty",html);
        using var denied=await web.Send("/Staff/Reports","anonymous");Assert.NotEqual(System.Net.HttpStatusCode.OK,denied.StatusCode);
    }
    [Fact]
    public async Task InvalidInvitationsAndRevokedApprovalPermissionsFailClosedAndExpiredRecoveryCanBeReissued()
    {
        await using var f=await Setup();
        foreach(string? token in new string?[]{null,"",new string('x',500),"invalid.token"})Assert.Null(await f.Service.InvitationAsync(token,Ct));
        string approved=await Approve(f);
        f.Time.Now=f.Time.Now.AddMinutes(31);
        Guid replacement=await f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Google","Reissue expired support case",Ct);
        Assert.NotEqual((await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Recoveries.Last().Id,replacement);
        string replacementToken=await f.Service.ApproveAsync(f.Actor(f.Second),f.Applicant.Id,replacement,"Independent ownership rechecked CASE-REISSUE",true,Ct);
        Assert.Null(await f.Service.InvitationAsync(approved,Ct));Assert.NotNull(await f.Service.InvitationAsync(replacementToken,Ct));
        var adminRole=await f.Database.Db.Roles.SingleAsync(r=>r.Name==PortalRoles.Administrator,Ct);
        var assignment=await f.Database.Db.UserRoles.SingleAsync(r=>r.UserId==Guid.Parse(f.Second.Id)&&r.RoleId==adminRole.Id,Ct);
        f.Database.Db.UserRoles.Remove(assignment);await f.Database.Db.SaveChangesAsync(Ct);
        Assert.Null(await f.Service.InvitationAsync(replacementToken,Ct));
        Assert.Contains((await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Events,e=>e.Action=="RecoveryExpired");
    }
    [Fact]
    public async Task ConcurrentOAuthCallbacksConsumeExactlyOnceAndCannotBindTwoProviderIdentities()
    {
        await using var f=await Setup();string token=await Approve(f);string connection=f.Database.Db.Database.GetConnectionString()!;
        async Task<bool> Redeem(string subject)
        {
            await using var db=new RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql(connection).Options);
            var roles=new RequestPortalAuthorizationStore(db,Options.Create(new ActiveDirectoryOptions()));await roles.EnsureInitializedAsync(Ct);
            var service=new ApplicantIdentityAdministration(f.Accounts,db,new PermissionAuthorizer(roles),new AuthenticationSchemeProvider(Options.Create(new AuthenticationOptions())),f.Time);
            try { await service.CompleteAsync(token,Proof(subject:subject),Ct);return true; } catch(InvalidOperationException){return false;}
        }
        var results=await Task.WhenAll(Redeem("concurrent-first"),Redeem("concurrent-second"));Assert.Single(results,r=>r);
        Assert.Single(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));Assert.Null(await f.Service.InvitationAsync(token,Ct));
        Assert.Single(await f.Database.Db.OutboxMessages.Where(m=>m.MessageType.Contains("ApplicantIdentityRecoveredMessage")).ToListAsync(Ct));
    }
    [Fact]
    public async Task PublicRecoveryEndpointRateLimitsRepeatedAttempts()
    {
        await using var f=await Setup();await using var web=await Web(f);
        for(int i=0;i<10;i++){using var response=await web.Send("/Account/IdentityRecovery","anonymous");Assert.Equal(System.Net.HttpStatusCode.OK,response.StatusCode);}
        using var limited=await web.Send("/Account/IdentityRecovery","anonymous");Assert.Equal(System.Net.HttpStatusCode.TooManyRequests,limited.StatusCode);
    }
    [Fact]
    public async Task RecoveryOutboxNotifiesTheRegisteredContactWithoutSecretsAndDoesNotRepeatProcessedWork()
    {
        await using var f=await Setup();string token=await Approve(f);await f.Service.CompleteAsync(token,Proof(),Ct);
        var sent=new List<RequestPortal.Common.Messaging.NotificationEnvelope>();
        var sender=MessageNavigationPageTests.Proxy.For<RequestPortal.Common.Messaging.INotificationSender>((_,args)=>{sent.Add((RequestPortal.Common.Messaging.NotificationEnvelope)args[0]!);return Task.CompletedTask;});
        var dispatcher=new RequestPortal.Infrastructure.Messaging.OutboxDispatcher(f.Database.Db,sender,null!,
            Options.Create(new RequestPortal.Infrastructure.Integration.FfpManagerBridgeOptions()),Options.Create(new RequestPortal.Infrastructure.Messaging.NotificationOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RequestPortal.Infrastructure.Messaging.OutboxDispatcher>.Instance,null!,null!);
        Assert.Equal(1,await dispatcher.DispatchBatchAsync(20,Ct));Assert.Equal(0,await dispatcher.DispatchBatchAsync(20,Ct));
        var message=Assert.Single(sent);Assert.Equal(f.Applicant.Email,message.Recipient);Assert.Contains("Google",message.Body);
        Assert.DoesNotContain(token,message.Body);Assert.DoesNotContain("new-verified-subject",message.Body);Assert.DoesNotContain("different-contact@example.test",message.Body);
    }
    [Fact]
    public async Task ApplicantsWithoutNotificationContactCannotIssueRecoveryInvitations()
    {
        await using var f=await Setup();f.Applicant.Email=null;f.Applicant.NormalizedEmail=null;await f.Database.Db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.InitiateAsync(f.Actor(f.First),f.Applicant.Id,"Google","Support case lacks verified contact",Ct));
        Assert.Empty((await f.Service.DetailsAsync(f.Actor(f.First),f.Applicant.Id,Ct))!.Recoveries);
        Assert.Empty(await f.Database.Db.OutboxMessages.ToListAsync(Ct));
    }
    [Fact]
    public async Task ConcurrentUnlinkingNeverRemovesTheFinalConfiguredIdentity()
    {
        await using var f=await Setup(true);var google=Proof();f.Accounts.Public.Link(f.Applicant.Id.ToString(),google,f.Applicant.Id.ToString());
        var identities=f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString());Assert.Equal(2,identities.Count);
        ClaimsPrincipal Owner(ExternalIdentityKey key) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier,f.Applicant.Id.ToString()) }.Concat(PublicExternalIdentity.SessionClaims(f.Accounts.Public.Find(f.Applicant.Id.ToString())!,Proof(key.ProviderId,key.SubjectId))),"fixture"));
        async Task<bool> Remove(ExternalIdentityKey key)
        {
            var accounts=new RequestPortalAccounts(f.Database.Db.Database.GetConnectionString()!);
            try { await accounts.UnlinkPublicIdentityAsync(Owner(identities.Single(i=>i.Key!=key).Key),key,["Google","Microsoft"],Ct);return true; }
            catch(Exception error) when(error is InvalidOperationException or UnauthorizedAccessException){return false;}
        }
        var results=await Task.WhenAll(identities.Select(identity=>Remove(identity.Key)));Assert.Single(results,r=>r);
        var remaining=Assert.Single(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Accounts.UnlinkPublicIdentityAsync(Owner(remaining.Key),remaining.Key,["Google","Microsoft"],Ct));
        Assert.Single(f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()));
    }
    [Fact]
    public async Task AnUnconfiguredProviderDoesNotCountAsAUsableReplacement()
    {
        await using var f=await Setup(true);var google=Proof();f.Accounts.Public.Link(f.Applicant.Id.ToString(),google,f.Applicant.Id.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Accounts.UnlinkPublicIdentityAsync(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier,f.Applicant.Id.ToString()) }.Concat(PublicExternalIdentity.SessionClaims(f.Accounts.Public.Find(f.Applicant.Id.ToString())!,Proof("Microsoft","old-identity"))),"fixture")),google.Key,["Google"],Ct));
        Assert.Equal(2,f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()).Count);
        var signedInWithGoogle=new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier,f.Applicant.Id.ToString()) }.Concat(PublicExternalIdentity.SessionClaims(f.Accounts.Public.Find(f.Applicant.Id.ToString())!,google)),"fixture"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Accounts.UnlinkPublicIdentityAsync(signedInWithGoogle,google.Key,["Google","Microsoft"],Ct));
        Assert.Equal(2,f.Accounts.Public.GetIdentities(f.Applicant.Id.ToString()).Count);
    }
}
