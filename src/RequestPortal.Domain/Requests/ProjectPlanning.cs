using System;

namespace RequestPortal.Domain.Requests;

public sealed class ProjectBudgetItem
{
    private ProjectBudgetItem()
    {
    }

    public ProjectBudgetItem(Guid id, Guid requestId, string description, decimal quantity, decimal unitCost, decimal ffpRequested, decimal organizationContribution, decimal otherFunding)
    {
        if (id == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Budget item and request ids are required.");
        }

        if (string.IsNullOrWhiteSpace(description) || quantity <= 0 || unitCost < 0 || ffpRequested < 0 || organizationContribution < 0 || otherFunding < 0)
        {
            throw new ArgumentException("Valid budget values are required.");
        }

        Id = id;
        RequestId = requestId;
        Description = description.Trim();
        Quantity = quantity;
        UnitCost = unitCost;
        FfpRequested = ffpRequested;
        OrganizationContribution = organizationContribution;
        OtherFunding = otherFunding;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal UnitCost { get; private set; }
    public decimal FfpRequested { get; private set; }
    public decimal OrganizationContribution { get; private set; }
    public decimal OtherFunding { get; private set; }
    public decimal TotalCost => Quantity * UnitCost;
}

public sealed class ProjectMilestone
{
    private ProjectMilestone()
    {
    }

    public ProjectMilestone(Guid id, Guid requestId, string title, string expectedOutcome, DateOnly targetDate, int sequence)
    {
        if (id == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Milestone and request ids are required.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Milestone title is required.", nameof(title));
        }

        Id = id;
        RequestId = requestId;
        Title = title.Trim();
        ExpectedOutcome = expectedOutcome?.Trim() ?? string.Empty;
        TargetDate = targetDate;
        Sequence = sequence;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string ExpectedOutcome { get; private set; } = string.Empty;
    public DateOnly TargetDate { get; private set; }
    public int Sequence { get; private set; }
}
