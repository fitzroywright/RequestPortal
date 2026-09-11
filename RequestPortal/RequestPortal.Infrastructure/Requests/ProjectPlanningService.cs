using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Requests;

public sealed class ProjectPlanningService : IProjectPlanningService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;

    public ProjectPlanningService(RequestPortalDbContext dbContext, IOrganizationAccessService organizationAccessService)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
    }

    public async Task<ProjectPlanningDetail> GetAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await GetProjectAsync(requestId, cancellationToken);
        if (!isStaff)
        {
            await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        }

        List<ProjectBudgetItemDetail> budget = await dbContext.ProjectBudgetItems.AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderBy(item => item.Description)
            .Select(item => new ProjectBudgetItemDetail(item.Id, item.Description, item.Quantity, item.UnitCost, item.Quantity * item.UnitCost, item.FfpRequested, item.OrganizationContribution, item.OtherFunding))
            .ToListAsync(cancellationToken);

        List<ProjectMilestoneDetail> milestones = await dbContext.ProjectMilestones.AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderBy(item => item.Sequence).ThenBy(item => item.TargetDate)
            .Select(item => new ProjectMilestoneDetail(item.Id, item.Title, item.ExpectedOutcome, item.TargetDate, item.Sequence))
            .ToListAsync(cancellationToken);

        return new ProjectPlanningDetail(budget, milestones, budget.Sum(item => item.TotalCost), budget.Sum(item => item.FfpRequested), budget.Sum(item => item.OrganizationContribution), budget.Sum(item => item.OtherFunding));
    }

    public async Task AddBudgetItemAsync(Guid userId, Guid requestId, string description, decimal quantity, decimal unitCost, decimal ffpRequested, decimal organizationContribution, decimal otherFunding, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await GetEditableProjectAsync(userId, requestId, cancellationToken);
        dbContext.ProjectBudgetItems.Add(new ProjectBudgetItem(Guid.NewGuid(), request.Id, description, quantity, unitCost, ffpRequested, organizationContribution, otherFunding));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveBudgetItemAsync(Guid userId, Guid requestId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await GetEditableProjectAsync(userId, requestId, cancellationToken);
        ProjectBudgetItem item = await dbContext.ProjectBudgetItems.SingleAsync(value => value.Id == itemId && value.RequestId == requestId, cancellationToken);
        dbContext.ProjectBudgetItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AddMilestoneAsync(Guid userId, Guid requestId, string title, string expectedOutcome, DateOnly targetDate, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await GetEditableProjectAsync(userId, requestId, cancellationToken);
        int sequence = await dbContext.ProjectMilestones.CountAsync(item => item.RequestId == requestId, cancellationToken) + 1;
        dbContext.ProjectMilestones.Add(new ProjectMilestone(Guid.NewGuid(), request.Id, title, expectedOutcome, targetDate, sequence));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMilestoneAsync(Guid userId, Guid requestId, Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await GetEditableProjectAsync(userId, requestId, cancellationToken);
        ProjectMilestone milestone = await dbContext.ProjectMilestones.SingleAsync(value => value.Id == milestoneId && value.RequestId == requestId, cancellationToken);
        dbContext.ProjectMilestones.Remove(milestone);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<AssistanceRequest> GetEditableProjectAsync(Guid userId, Guid requestId, CancellationToken cancellationToken)
    {
        AssistanceRequest request = await GetProjectAsync(requestId, cancellationToken);
        await EnsureAccessAsync(userId, request.OrganizationId, cancellationToken);
        if (request.Status != RequestStatus.Draft && request.Status != RequestStatus.MoreInformationRequired)
        {
            throw new InvalidOperationException("Project planning can only be edited while the request is a draft or requires more information.");
        }
        return request;
    }

    private async Task<AssistanceRequest> GetProjectAsync(Guid requestId, CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.SingleAsync(item => item.Id == requestId, cancellationToken);
        if (request.Kind != RequestKind.Project)
        {
            throw new InvalidOperationException("The request is not a project request.");
        }
        return request;
    }

    private async Task EnsureAccessAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        if (!await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken))
        {
            throw new UnauthorizedAccessException("The current user cannot access this project.");
        }
    }
}
