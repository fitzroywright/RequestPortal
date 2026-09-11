using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Requests;

public sealed record ProjectBudgetItemDetail(Guid Id, string Description, decimal Quantity, decimal UnitCost, decimal TotalCost, decimal FfpRequested, decimal OrganizationContribution, decimal OtherFunding);
public sealed record ProjectMilestoneDetail(Guid Id, string Title, string ExpectedOutcome, DateOnly TargetDate, int Sequence);
public sealed record ProjectPlanningDetail(IReadOnlyList<ProjectBudgetItemDetail> Budget, IReadOnlyList<ProjectMilestoneDetail> Milestones, decimal TotalCost, decimal TotalFfpRequested, decimal TotalOrganizationContribution, decimal TotalOtherFunding);

public interface IProjectPlanningService
{
    Task<ProjectPlanningDetail> GetAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default);
    Task AddBudgetItemAsync(Guid userId, Guid requestId, string description, decimal quantity, decimal unitCost, decimal ffpRequested, decimal organizationContribution, decimal otherFunding, CancellationToken cancellationToken = default);
    Task RemoveBudgetItemAsync(Guid userId, Guid requestId, Guid itemId, CancellationToken cancellationToken = default);
    Task AddMilestoneAsync(Guid userId, Guid requestId, string title, string expectedOutcome, DateOnly targetDate, CancellationToken cancellationToken = default);
    Task RemoveMilestoneAsync(Guid userId, Guid requestId, Guid milestoneId, CancellationToken cancellationToken = default);
}
