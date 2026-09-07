using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Security;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Countries;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Demo;

public sealed class DemoDataSeeder
{
    public const string RepresentativeEmail = "representative@demo.local";
    public const string AdministratorEmail = "orgadmin@demo.local";
    public const string PendingRepresentativeEmail = "pending@demo.local";
    public const string StaffEmail = "staff@demo.local";

    private static readonly Guid eachUnitId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly RequestPortalDbContext dbContext;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly RoleManager<IdentityRole<Guid>> roleManager;
    private readonly IConfiguration configuration;
    private readonly IClock clock;

    public DemoDataSeeder(
        RequestPortalDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        IConfiguration configuration,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.userManager = userManager;
        this.roleManager = roleManager;
        this.configuration = configuration;
        this.clock = clock;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Demo:Enabled", false))
        {
            return;
        }

        if (configuration.GetValue("Demo:EnsureDatabase", false))
        {
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await EnsureCountryConfigurationAsync(cancellationToken);

        ApplicationUser staff = await EnsureUserAsync(StaffEmail, "Demo FFP Staff", true);
        ApplicationUser administrator = await EnsureUserAsync(AdministratorEmail, "Grace Brown", false);
        ApplicationUser representative = await EnsureUserAsync(RepresentativeEmail, "Michael Campbell", false);
        await EnsureUserAsync(PendingRepresentativeEmail, "Keisha Morgan", false);

        await EnsureRoleAsync(PortalRoles.FfpReviewer);
        await EnsureRoleAsync(PortalRoles.FfpAdministrator);
        await EnsureUserRoleAsync(staff, PortalRoles.FfpReviewer);
        await EnsureUserRoleAsync(staff, PortalRoles.FfpAdministrator);

        Organization? organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleOrDefaultAsync(item => item.Name == "Hope Community Outreach", cancellationToken);

        if (organization is null)
        {
            DateTimeOffset now = clock.UtcNow;
            organization = new Organization(Guid.NewGuid(), "Hope Community Outreach", "JM", administrator.Id, now);
            organization.RequestMembership(Guid.NewGuid(), administrator.Id, now);
            organization.Approve(staff.Id, now);
            OrganizationMembership representativeMembership = organization.RequestMembership(Guid.NewGuid(), representative.Id, now);
            representativeMembership.Approve(administrator.Id, now);
            dbContext.Organizations.Add(organization);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        bool hasSubmittedRequest = await dbContext.AssistanceRequests
            .AsNoTracking()
            .AnyAsync(
                request => request.OrganizationId == organization.Id && request.Status == RequestStatus.Submitted,
                cancellationToken);

        if (!hasSubmittedRequest)
        {
            DateTimeOffset now = clock.UtcNow;
            AssistanceRequest request = new(
                Guid.NewGuid(),
                $"RP-{now:yyyyMMdd}-DEMO01",
                organization.Id,
                representative.Id,
                now);
            request.SetSummary("School supplies for children preparing for the new school term.");
            request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "School bags", 25, eachUnitId));
            request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Exercise books", 50, eachUnitId));
            request.Submit(representative.Id, now);
            dbContext.AssistanceRequests.Add(request);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsureCountryConfigurationAsync(CancellationToken cancellationToken)
    {
        bool exists = await dbContext.CountryConfigurations
            .AsNoTracking()
            .AnyAsync(item => item.CountryCode == "JM", cancellationToken);

        if (exists)
        {
            return;
        }

        CountryConfiguration jamaica = new(
            "JM",
            "Jamaica",
            "America/Jamaica",
            "en-JM");

        dbContext.CountryConfigurations.Add(jamaica);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ApplicationUser> EnsureUserAsync(string email, string displayName, bool isInternalStaff)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            return user;
        }

        user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            IsInternalStaff = isInternalStaff,
            DirectoryUserName = isInternalStaff ? "demo.staff" : null,
            CreatedUtc = clock.UtcNow
        };

        IdentityResult result = await userManager.CreateAsync(user);
        if (!result.Succeeded)
        {
            string detail = string.Join("; ", result.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"Unable to create demo user '{email}'. {detail}");
        }

        return user;
    }

    private async Task EnsureRoleAsync(string roleName)
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            IdentityResult result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Unable to create demo role '{roleName}'.");
            }
        }
    }

    private async Task EnsureUserRoleAsync(ApplicationUser user, string roleName)
    {
        if (!await userManager.IsInRoleAsync(user, roleName))
        {
            IdentityResult result = await userManager.AddToRoleAsync(user, roleName);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Unable to assign demo role '{roleName}'.");
            }
        }
    }
}
