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
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.ReferenceData;
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

        await EnsureCountryConfigurationAsync(cancellationToken);
        await EnsureReferenceDataAsync(cancellationToken);

        ApplicationUser staff = await EnsureUserAsync(StaffEmail, "Demo FFP Staff", true);
        ApplicationUser administrator = await EnsureUserAsync(AdministratorEmail, "Grace Brown", false);
        ApplicationUser representative = await EnsureUserAsync(RepresentativeEmail, "Michael Campbell", false);
        ApplicationUser pendingRepresentative = await EnsureUserAsync(PendingRepresentativeEmail, "Keisha Morgan", false);

        await EnsureRoleAsync(PortalRoles.FfpReviewer);
        await EnsureRoleAsync(PortalRoles.FfpAdministrator);
        await EnsureUserRoleAsync(staff, PortalRoles.FfpReviewer);
        await EnsureUserRoleAsync(staff, PortalRoles.FfpAdministrator);

        Organization organization = await EnsureApprovedOrganizationAsync(
            staff,
            administrator,
            representative,
            pendingRepresentative,
            cancellationToken);

        await EnsurePendingOrganizationAsync(pendingRepresentative, cancellationToken);
        await EnsureNotificationPreferencesAsync(administrator, representative, pendingRepresentative, cancellationToken);
        await EnsureSubmittedGoodsRequestAsync(organization, representative, cancellationToken);
        await EnsureSubmittedProjectRequestAsync(organization, administrator, cancellationToken);
        await EnsureMoreInformationRequestAsync(organization, representative, staff, cancellationToken);
        await EnsureApprovedRequestAsync(organization, representative, staff, cancellationToken);
        await EnsurePendingFulfilmentRequestAsync(organization, administrator, staff, cancellationToken);
    }

    private async Task<Organization> EnsureApprovedOrganizationAsync(
        ApplicationUser staff,
        ApplicationUser administrator,
        ApplicationUser representative,
        ApplicationUser pendingRepresentative,
        CancellationToken cancellationToken)
    {
        Organization? organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleOrDefaultAsync(item => item.Name == "Hope Community Outreach", cancellationToken);

        if (organization is null)
        {
            DateTimeOffset now = clock.UtcNow;
            organization = new Organization(Guid.NewGuid(), "Hope Community Outreach", "JM", administrator.Id, now);
            organization.UpdateProfile(
                "CO-2018-0421",
                "Community-Based Organization",
                "14 Hope Road, Spanish Town, St. Catherine",
                "+1 876 555 0142",
                "info@hopecommunity.demo",
                "Grace Brown",
                DateOnly.FromDateTime(now.UtcDateTime.Date.AddYears(2)));
            organization.RequestMembership(Guid.NewGuid(), administrator.Id, now);
            organization.Approve(staff.Id, now, "Verified for Request Portal demonstration.");
            OrganizationMembership representativeMembership = organization.RequestMembership(Guid.NewGuid(), representative.Id, now);
            representativeMembership.Approve(administrator.Id, now);
            organization.RequestMembership(Guid.NewGuid(), pendingRepresentative.Id, now);
            dbContext.Organizations.Add(organization);
            await dbContext.SaveChangesAsync(cancellationToken);
            return organization;
        }

        if (!organization.Memberships.Any(item => item.UserId == representative.Id))
        {
            OrganizationMembership membership = organization.RequestMembership(Guid.NewGuid(), representative.Id, clock.UtcNow);
            membership.Approve(administrator.Id, clock.UtcNow);
        }

        if (!organization.Memberships.Any(item => item.UserId == pendingRepresentative.Id))
        {
            organization.RequestMembership(Guid.NewGuid(), pendingRepresentative.Id, clock.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return organization;
    }

    private async Task EnsurePendingOrganizationAsync(
        ApplicationUser pendingRepresentative,
        CancellationToken cancellationToken)
    {
        bool exists = await dbContext.Organizations
            .AsNoTracking()
            .AnyAsync(item => item.Name == "New Hope Youth Foundation", cancellationToken);

        if (exists)
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow;
        Organization organization = new(Guid.NewGuid(), "New Hope Youth Foundation", "JM", pendingRepresentative.Id, now);
        organization.UpdateProfile(
            "NHYF-2026-009",
            "Youth Development Organization",
            "8 Market Street, May Pen, Clarendon",
            "+1 876 555 0199",
            "office@newhopeyouth.demo",
            "Keisha Morgan",
            DateOnly.FromDateTime(now.UtcDateTime.Date.AddYears(1)));
        organization.RequestMembership(Guid.NewGuid(), pendingRepresentative.Id, now);
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSubmittedGoodsRequestAsync(
        Organization organization,
        ApplicationUser representative,
        CancellationToken cancellationToken)
    {
        const string summary = "School supplies for children preparing for the new school term.";
        if (await RequestExistsAsync(organization.Id, summary, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.AddDays(-3);
        AssistanceRequest request = CreateGoodsRequest(
            organization,
            representative,
            now,
            "DEMO01",
            summary,
            "Education",
            "School Supplies",
            "Education Support",
            RequestPriority.Normal,
            "Spanish Town, St. Catherine");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "School bags", 25, eachUnitId));
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Exercise books", 50, eachUnitId));
        request.Submit(representative.Id, now.AddMinutes(20));
        dbContext.AssistanceRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSubmittedProjectRequestAsync(
        Organization organization,
        ApplicationUser administrator,
        CancellationToken cancellationToken)
    {
        const string summary = "Rehabilitation of the Hope Community water storage and distribution system.";
        if (await RequestExistsAsync(organization.Id, summary, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.AddDays(-5);
        AssistanceRequest request = new(
            Guid.NewGuid(),
            $"RP-{now:yyyyMMdd}-PROJ01",
            organization.Id,
            administrator.Id,
            now);
        request.SetKind(RequestKind.Project);
        request.SetSummary(summary);
        request.SetDetails(
            null,
            "Water & Sanitation",
            "Community Water",
            "Community Development",
            RequestPriority.High,
            "Spanish Town, St. Catherine",
            "The existing storage tanks and distribution lines are unreliable, leaving approximately 180 residents without dependable water during service interruptions.");

        ProjectProposal proposal = new(Guid.NewGuid(), request.Id, administrator.Id, now);
        proposal.Update(
            "Hope Community Water Rehabilitation Project",
            "<h2>Executive Summary</h2><p>Hope Community Outreach proposes the rehabilitation of the community water storage and distribution system serving approximately 180 residents.</p><h2>Statement of Need</h2><p>The current tanks are deteriorated and the distribution network experiences frequent leaks. During municipal service interruptions, vulnerable households have no dependable reserve supply.</p><h2>Objectives</h2><ul><li>Restore safe water storage capacity.</li><li>Reduce distribution losses.</li><li>Improve resilience for vulnerable households.</li></ul><h2>Implementation Approach</h2><p>The project will replace two storage tanks, repair priority distribution lines, install isolation valves and complete community training for system monitoring and basic maintenance.</p><h2>Sustainability</h2><p>The organization will establish a maintenance committee and retain a small monthly maintenance reserve.</p>",
            administrator.Id,
            now.AddHours(2));

        dbContext.AssistanceRequests.Add(request);
        dbContext.ProjectProposals.Add(proposal);
        dbContext.ProjectBudgetItems.AddRange(
            new ProjectBudgetItem(Guid.NewGuid(), request.Id, "2,000 gallon water tanks", 2, 185000m, 330000m, 40000m, 0m),
            new ProjectBudgetItem(Guid.NewGuid(), request.Id, "PVC pipe, fittings and valves", 1, 210000m, 180000m, 30000m, 0m),
            new ProjectBudgetItem(Guid.NewGuid(), request.Id, "Installation labour and testing", 1, 120000m, 90000m, 30000m, 0m));
        dbContext.ProjectMilestones.AddRange(
            new ProjectMilestone(Guid.NewGuid(), request.Id, "Procurement and site preparation", "Materials procured and installation areas prepared.", DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(30)), 1),
            new ProjectMilestone(Guid.NewGuid(), request.Id, "Tank and distribution installation", "New tanks installed and priority distribution leaks repaired.", DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(60)), 2),
            new ProjectMilestone(Guid.NewGuid(), request.Id, "Testing, training and handover", "System tested, community maintenance team trained and project handed over.", DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(75)), 3));
        request.Submit(administrator.Id, now.AddHours(3));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureMoreInformationRequestAsync(
        Organization organization,
        ApplicationUser representative,
        ApplicationUser staff,
        CancellationToken cancellationToken)
    {
        const string summary = "Replacement beds and mattresses for the community shelter.";
        if (await RequestExistsAsync(organization.Id, summary, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.AddDays(-7);
        AssistanceRequest request = CreateGoodsRequest(
            organization,
            representative,
            now,
            "INFO01",
            summary,
            "Emergency Relief",
            "Shelter Support",
            "Disaster Response",
            RequestPriority.High,
            "Spanish Town, St. Catherine");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Single beds", 10, eachUnitId));
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Mattresses", 10, eachUnitId));
        request.Submit(representative.Id, now.AddMinutes(15));
        request.RequestMoreInformation(
            staff.Id,
            now.AddDays(1),
            "Please upload the current shelter occupancy list and two recent photographs of the sleeping area.");
        dbContext.AssistanceRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureApprovedRequestAsync(
        Organization organization,
        ApplicationUser representative,
        ApplicationUser staff,
        CancellationToken cancellationToken)
    {
        const string summary = "Emergency roofing materials for three vulnerable households.";
        if (await RequestExistsAsync(organization.Id, summary, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.AddDays(-9);
        AssistanceRequest request = CreateGoodsRequest(
            organization,
            representative,
            now,
            "APPR01",
            summary,
            "Housing",
            "Roof Repair",
            "Housing Support",
            RequestPriority.High,
            "St. Catherine",
            "Three households have significant roof damage following severe weather.");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Roofing sheets", 36, eachUnitId));
        request.Submit(representative.Id, now.AddMinutes(30));
        request.Approve(staff.Id, now.AddDays(2));
        dbContext.AssistanceRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsurePendingFulfilmentRequestAsync(
        Organization organization,
        ApplicationUser administrator,
        ApplicationUser staff,
        CancellationToken cancellationToken)
    {
        const string summary = "Hygiene kits for the Hope Community senior support programme.";
        if (await RequestExistsAsync(organization.Id, summary, cancellationToken))
        {
            return;
        }

        DateTimeOffset now = clock.UtcNow.AddDays(-12);
        AssistanceRequest request = CreateGoodsRequest(
            organization,
            administrator,
            now,
            "FULF01",
            summary,
            "Health",
            "Hygiene Supplies",
            "Health Support",
            RequestPriority.Normal,
            "Spanish Town, St. Catherine");
        request.AddItem(new AssistanceRequestItem(Guid.NewGuid(), request.Id, "Personal hygiene kits", 40, eachUnitId));
        request.Submit(administrator.Id, now.AddMinutes(20));
        request.Approve(staff.Id, now.AddDays(1));
        request.MarkPendingFulfilment(staff.Id, now.AddDays(2));

        Fulfilment fulfilment = new(
            Guid.NewGuid(),
            request.Id,
            organization.Id,
            FulfilmentSource.LocalIcp,
            now.AddDays(2));
        fulfilment.MarkInProgress("ICP-DEMO-1001");

        IntegrationLink integration = new(
            Guid.NewGuid(),
            request.Id,
            "Local ICP",
            now.AddDays(2));
        integration.MarkSynchronized("ICP-DEMO-1001", now.AddDays(2).AddMinutes(5));

        dbContext.AssistanceRequests.Add(request);
        dbContext.Fulfilments.Add(fulfilment);
        dbContext.IntegrationLinks.Add(integration);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private AssistanceRequest CreateGoodsRequest(
        Organization organization,
        ApplicationUser user,
        DateTimeOffset createdUtc,
        string referenceSuffix,
        string summary,
        string category,
        string subcategory,
        string programCategory,
        RequestPriority priority,
        string location,
        string? circumstances = null)
    {
        AssistanceRequest request = new(
            Guid.NewGuid(),
            $"RP-{createdUtc:yyyyMMdd}-{referenceSuffix}",
            organization.Id,
            user.Id,
            createdUtc);
        request.SetSummary(summary);
        request.SetDetails(
            null,
            category,
            subcategory,
            programCategory,
            priority,
            location,
            circumstances ?? summary);
        return request;
    }

    private Task<bool> RequestExistsAsync(
        Guid organizationId,
        string summary,
        CancellationToken cancellationToken)
    {
        return dbContext.AssistanceRequests
            .AsNoTracking()
            .AnyAsync(
                request => request.OrganizationId == organizationId && request.Summary == summary,
                cancellationToken);
    }

    private async Task EnsureCountryConfigurationAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.CountryConfigurations
            .AsNoTracking()
            .AnyAsync(item => item.CountryCode == "JM", cancellationToken))
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

    private async Task EnsureReferenceDataAsync(CancellationToken cancellationToken)
    {
        if (!await dbContext.UnitsOfMeasure.AsNoTracking().AnyAsync(cancellationToken))
        {
            dbContext.UnitsOfMeasure.AddRange(
                new UnitOfMeasure(eachUnitId, "EA", "Each"),
                new UnitOfMeasure(Guid.Parse("22222222-2222-2222-2222-222222222222"), "BOX", "Box"),
                new UnitOfMeasure(Guid.Parse("33333333-3333-3333-3333-333333333333"), "CASE", "Case"),
                new UnitOfMeasure(Guid.Parse("44444444-4444-4444-4444-444444444444"), "KG", "Kilogram"),
                new UnitOfMeasure(Guid.Parse("55555555-5555-5555-5555-555555555555"), "L", "Litre"));
        }

        if (!await dbContext.RequestCategories.AsNoTracking().AnyAsync(cancellationToken))
        {
            dbContext.RequestCategories.AddRange(
                new RequestCategory(Guid.NewGuid(), "Education"),
                new RequestCategory(Guid.NewGuid(), "Health"),
                new RequestCategory(Guid.NewGuid(), "Housing"),
                new RequestCategory(Guid.NewGuid(), "Water & Sanitation"),
                new RequestCategory(Guid.NewGuid(), "Livelihoods"),
                new RequestCategory(Guid.NewGuid(), "Emergency Relief"));
        }

        if (!await dbContext.ProgramClassifications.AsNoTracking().AnyAsync(cancellationToken))
        {
            dbContext.ProgramClassifications.AddRange(
                new ProgramClassification(Guid.NewGuid(), "Education Support"),
                new ProgramClassification(Guid.NewGuid(), "Health Support"),
                new ProgramClassification(Guid.NewGuid(), "Community Development"),
                new ProgramClassification(Guid.NewGuid(), "Housing Support"),
                new ProgramClassification(Guid.NewGuid(), "Disaster Response"));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureNotificationPreferencesAsync(
        ApplicationUser administrator,
        ApplicationUser representative,
        ApplicationUser pendingRepresentative,
        CancellationToken cancellationToken)
    {
        ApplicationUser[] users = [administrator, representative, pendingRepresentative];
        foreach (ApplicationUser user in users)
        {
            bool hasEmail = await dbContext.NotificationPreferences
                .AsNoTracking()
                .AnyAsync(
                    item => item.UserId == user.Id && item.Channel == NotificationChannel.Email,
                    cancellationToken);

            if (!hasEmail && !string.IsNullOrWhiteSpace(user.Email))
            {
                dbContext.NotificationPreferences.Add(
                    new NotificationPreference(
                        Guid.NewGuid(),
                        user.Id,
                        NotificationChannel.Email,
                        user.Email,
                        true));
            }
        }

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
        if (await roleManager.RoleExistsAsync(roleName))
        {
            return;
        }

        IdentityResult result = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to create demo role '{roleName}'.");
        }
    }

    private async Task EnsureUserRoleAsync(ApplicationUser user, string roleName)
    {
        if (await userManager.IsInRoleAsync(user, roleName))
        {
            return;
        }

        IdentityResult result = await userManager.AddToRoleAsync(user, roleName);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to assign demo role '{roleName}'.");
        }
    }
}
