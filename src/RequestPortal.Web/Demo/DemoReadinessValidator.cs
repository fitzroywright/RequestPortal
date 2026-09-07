using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Security;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Demo;

public sealed class DemoReadinessValidator
{
    private readonly RequestPortalDbContext dbContext;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IConfiguration configuration;

    public DemoReadinessValidator(
        RequestPortalDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration)
    {
        this.dbContext = dbContext;
        this.userManager = userManager;
        this.configuration = configuration;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue("Demo:Enabled", false))
        {
            return;
        }

        List<string> failures = new();

        ApplicationUser? staff = await userManager.FindByEmailAsync(DemoDataSeeder.StaffEmail);
        ApplicationUser? administrator = await userManager.FindByEmailAsync(DemoDataSeeder.AdministratorEmail);
        ApplicationUser? representative = await userManager.FindByEmailAsync(DemoDataSeeder.RepresentativeEmail);
        ApplicationUser? pending = await userManager.FindByEmailAsync(DemoDataSeeder.PendingRepresentativeEmail);

        Require(staff is not null, "FFP demo staff persona is missing.", failures);
        Require(administrator is not null, "Organization administrator persona is missing.", failures);
        Require(representative is not null, "Approved representative persona is missing.", failures);
        Require(pending is not null, "Pending representative persona is missing.", failures);

        if (staff is not null)
        {
            Require(
                await userManager.IsInRoleAsync(staff, PortalRoles.FfpReviewer),
                "FFP demo staff is not assigned the reviewer role.",
                failures);
        }

        Organization? approvedOrganization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Name == "Hope Community Outreach", cancellationToken);
        Organization? pendingOrganization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Name == "New Hope Youth Foundation", cancellationToken);

        Require(
            approvedOrganization?.Status == OrganizationStatus.Approved,
            "Approved demo organization is missing or not approved.",
            failures);
        Require(
            pendingOrganization?.Status == OrganizationStatus.PendingApproval,
            "Pending organization approval scenario is missing.",
            failures);

        if (approvedOrganization is not null && administrator is not null && representative is not null && pending is not null)
        {
            List<OrganizationMembership> memberships = await dbContext.OrganizationMemberships
                .AsNoTracking()
                .Where(item => item.OrganizationId == approvedOrganization.Id)
                .ToListAsync(cancellationToken);

            Require(
                memberships.Any(item =>
                    item.UserId == administrator.Id &&
                    item.Status == MembershipStatus.Approved &&
                    item.Role == OrganizationRole.Administrator),
                "Approved organization administrator membership is missing.",
                failures);
            Require(
                memberships.Any(item =>
                    item.UserId == representative.Id &&
                    item.Status == MembershipStatus.Approved),
                "Approved representative membership is missing.",
                failures);
            Require(
                memberships.Any(item =>
                    item.UserId == pending.Id &&
                    item.Status == MembershipStatus.Pending),
                "Pending membership approval scenario is missing.",
                failures);
        }

        Require(
            await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(
                item => item.Kind == RequestKind.GoodsAndServices && item.Status == RequestStatus.Submitted,
                cancellationToken),
            "Submitted goods/services request scenario is missing.",
            failures);
        Require(
            await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(
                item => item.Status == RequestStatus.MoreInformationRequired,
                cancellationToken),
            "More-information-required request scenario is missing.",
            failures);
        Require(
            await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(
                item => item.Status == RequestStatus.Approved,
                cancellationToken),
            "Approved request awaiting fulfilment scenario is missing.",
            failures);
        Require(
            await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(
                item => item.Status == RequestStatus.PendingFulfilment,
                cancellationToken),
            "Pending fulfilment scenario is missing.",
            failures);
        Require(
            await dbContext.Fulfilments.AsNoTracking().AnyAsync(cancellationToken),
            "Fulfilment record is missing.",
            failures);
        Require(
            await dbContext.IntegrationLinks.AsNoTracking().AnyAsync(
                item => item.Status == SynchronizationStatus.Synchronized,
                cancellationToken),
            "Synchronized integration example is missing.",
            failures);

        AssistanceRequest? projectRequest = await dbContext.AssistanceRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Kind == RequestKind.Project && item.Status == RequestStatus.Submitted,
                cancellationToken);
        Require(projectRequest is not null, "Submitted project request scenario is missing.", failures);

        if (projectRequest is not null)
        {
            Require(
                await dbContext.ProjectProposals.AsNoTracking().AnyAsync(
                    item => item.RequestId == projectRequest.Id && item.RichContentHtml.Length > 100,
                    cancellationToken),
                "Project proposal narrative is missing.",
                failures);
            Require(
                await dbContext.ProjectBudgetItems.AsNoTracking().AnyAsync(
                    item => item.RequestId == projectRequest.Id,
                    cancellationToken),
                "Project budget is missing.",
                failures);
            Require(
                await dbContext.ProjectMilestones.AsNoTracking().AnyAsync(
                    item => item.RequestId == projectRequest.Id,
                    cancellationToken),
                "Project milestone plan is missing.",
                failures);
        }

        Require(
            await dbContext.RequestCategories.AsNoTracking().AnyAsync(cancellationToken),
            "Request category reference data is missing.",
            failures);
        Require(
            await dbContext.ProgramClassifications.AsNoTracking().AnyAsync(cancellationToken),
            "Program classification reference data is missing.",
            failures);
        Require(
            await dbContext.UnitsOfMeasure.AsNoTracking().AnyAsync(cancellationToken),
            "Unit-of-measure reference data is missing.",
            failures);
        Require(
            await dbContext.CountryConfigurations.AsNoTracking().AnyAsync(
                item => item.CountryCode == "JM",
                cancellationToken),
            "Jamaica country configuration is missing.",
            failures);
        Require(
            await dbContext.NotificationPreferences.AsNoTracking().AnyAsync(cancellationToken),
            "Notification preferences are missing.",
            failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "SF-DEMO-READINESS-001: Request Portal demo readiness validation failed: " +
                string.Join(" | ", failures));
        }
    }

    private static void Require(bool condition, string message, ICollection<string> failures)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }
}
